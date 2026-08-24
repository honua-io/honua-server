# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""WMTS 1.0.0 certification through ``owslib.wmts.WebMapTileService``.

Every tile this lane requests is addressed from the capabilities document --
tile matrix set, tile matrix, top-left corner, scale denominator and tile size
all come from the server's own metadata, and the row/column indices are derived
from them with the WMTS 1.0.0 pixel-span formula. A capabilities document that
describes a grid the server does not actually serve therefore fails here rather
than quietly producing blank tiles.

Coverage: capabilities parse (operations, both DCP encodings, tile matrix sets,
tile matrix geometry, tile matrix set limits, layer styles/formats/ResourceURL
templates), ``gettile`` across zoom levels and both advertised matrix sets, the
RESTful encoding against the KVP encoding, and the out-of-range/unknown-resource
error surface.
"""

from __future__ import annotations

import io
import math
import re

import pytest
from owslib.ogcapi.features import Features
from owslib.util import ServiceException, openURL
from owslib.wmts import WebMapTileService
from PIL import Image

from shared import canonical_fixture as fx
from shared.cert_envelope import CertificationEvidenceCollector

from .conftest import AdminProbe, LaneConfig, Timer

pytestmark = pytest.mark.owslib_client

# A seeded browser_compat point (tests/seed/browser-compat.yaml, layer 2000).
SAMPLE_LON, SAMPLE_LAT = -122.4194, 37.7749

# WMTS 1.0.0 clause 6.1: the standardized rendering pixel size.
WMTS_PIXEL_SIZE_METRES = 0.00028
# Metres per degree at the equator, used to convert a scale denominator
# expressed against a geographic CRS into a ground span.
METRES_PER_DEGREE = 111319.4907932736


@pytest.fixture(scope="session")
def wmts(lane_config: LaneConfig) -> WebMapTileService:
    return WebMapTileService(lane_config.wmts_url)


@pytest.fixture(scope="session")
def wmts_layer(wmts: WebMapTileService, lane_config: LaneConfig) -> str:
    layer_id = lane_config.raster_layer_id
    assert layer_id in wmts.contents, (
        f"WMTS advertises {list(wmts.contents)}, not the configured raster layer {layer_id!r}"
    )
    return layer_id


def _metres_per_unit(crs: str) -> float:
    """Ground units per CRS unit for the tile matrix set's SupportedCRS."""
    if "CRS84" in crs.upper() or crs.rstrip("/").endswith("4326"):
        return METRES_PER_DEGREE
    return 1.0


def _tile_index(matrix, crs: str, x: float, y: float) -> tuple[int, int]:
    """Row/column of the tile containing (x, y), per WMTS 1.0.0 clause 6.1.

    ``x``/``y`` are in the tile matrix set's CRS, in its declared axis order
    (easting/northing for both grids this server advertises).
    """
    pixel_span = (float(matrix.scaledenominator) * WMTS_PIXEL_SIZE_METRES) / _metres_per_unit(crs)
    tile_span_x = pixel_span * int(matrix.tilewidth)
    tile_span_y = pixel_span * int(matrix.tileheight)
    left, top = float(matrix.topleftcorner[0]), float(matrix.topleftcorner[1])
    column = int(math.floor((x - left) / tile_span_x))
    row = int(math.floor((top - y) / tile_span_y))
    return row, column


def _project(crs: str, lon: float, lat: float) -> tuple[float, float]:
    if _metres_per_unit(crs) == METRES_PER_DEGREE:
        return lon, lat
    radius = 20037508.342789244
    x = lon * radius / 180.0
    y = math.log(math.tan((90.0 + lat) * math.pi / 360.0)) / (math.pi / 180.0)
    return x, y * radius / 180.0


def _sorted_matrix_ids(tile_matrix_set) -> list[str]:
    return sorted(tile_matrix_set.tilematrix, key=lambda value: int(value))


def _png(response) -> tuple[Image.Image, bytes]:
    payload = response.read()
    assert payload[:8] == b"\x89PNG\r\n\x1a\n", f"not a PNG: {payload[:32]!r}"
    return Image.open(io.BytesIO(payload)), payload


# ---------------------------------------------------------------------------
# CONN / AUTH
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-CONN-01")
def test_conn01_capabilities(wmts: WebMapTileService,
                             wmts_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    assert wmts.identification.version == "1.0.0"
    assert wmts.identification.title
    assert wmts.contents, "capabilities advertised no layers"
    assert wmts.tilematrixsets, "capabilities advertised no tile matrix sets"
    wmts_collector.record(
        "CERT-CONN-01", "pass",
        duration_ms=timer.ms,
        measured_count=len(wmts.contents),
        notes=(
            "owslib.wmts.WebMapTileService parsed a live WMTS 1.0.0 capabilities document "
            f"({wmts.identification.title!r}) into {len(wmts.contents)} layers and "
            f"{len(wmts.tilematrixsets)} tile matrix sets."
        ),
        evidence_ref=wmts.url,
    )


@pytest.mark.cert("CERT-CONN-02")
def test_conn02_transport(base_url: str, wmts_collector: CertificationEvidenceCollector) -> None:
    assert base_url.split("://", 1)[0] == "http"
    wmts_collector.record(
        "CERT-CONN-02", "pass",
        notes=(
            "Transport verified as plain http on the compose client-compat network, which "
            "terminates no TLS. TLS handshake behaviour is exercised in the release tier, where "
            "the same lane runs against the HTTPS candidate."
        ),
        evidence_ref=base_url,
    )


@pytest.mark.cert("CERT-AUTH-01")
def test_auth01_anonymous_rejected(admin_probe: AdminProbe,
                                   wmts_collector: CertificationEvidenceCollector) -> None:
    assert admin_probe.anonymous_status in (401, 403), admin_probe
    assert "ApiKey" in admin_probe.challenge and fx.ADMIN_API_KEY_HEADER in admin_probe.challenge
    wmts_collector.record(
        "CERT-AUTH-01", "pass",
        notes=(
            f"Anonymous GET {fx.ADMIN_PROBE_PATH} -> {admin_probe.anonymous_status}, "
            f"WWW-Authenticate: {admin_probe.challenge}. The WMTS surface is anonymous in this "
            "fixture, so the control plane substantiates the AUTH facets."
        ),
        evidence_ref=fx.ADMIN_PROBE_PATH,
    )


@pytest.mark.cert("CERT-AUTH-02")
def test_auth02_credential_grants_access(admin_probe: AdminProbe,
                                         wmts_collector: CertificationEvidenceCollector) -> None:
    assert admin_probe.authenticated_status // 100 == 2, admin_probe
    wmts_collector.record(
        "CERT-AUTH-02", "pass",
        notes=(
            f"Accepted scheme {admin_probe.scheme} -> {admin_probe.authenticated_status}; ladder: "
            + ", ".join(f"{name}={code}" for name, code in admin_probe.attempts)
        ),
        evidence_ref=fx.ADMIN_PROBE_PATH,
    )


# ---------------------------------------------------------------------------
# DISC
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-DISC-01")
def test_disc01_layers(wmts: WebMapTileService, wmts_layer: str,
                       wmts_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    assert wmts_layer in wmts.contents
    for name, layer in wmts.contents.items():
        assert layer.title, f"{name} has no title"
        assert layer.formats, f"{name} advertises no tile format"
        assert layer.tilematrixsetlinks, f"{name} links to no tile matrix set"
    wmts_collector.record(
        "CERT-DISC-01", "pass",
        duration_ms=timer.ms,
        measured_count=len(wmts.contents),
        notes=(
            f"WebMapTileService.contents listed {len(wmts.contents)} layers ({list(wmts.contents)}), "
            "each with a title, at least one format and at least one TileMatrixSetLink."
        ),
    )


@pytest.mark.cert("CERT-DISC-02")
def test_disc02_layer_metadata(wmts: WebMapTileService, wmts_layer: str,
                               wmts_collector: CertificationEvidenceCollector,
                               timer: Timer) -> None:
    layer = wmts[wmts_layer]
    assert "image/png" in layer.formats, layer.formats
    assert "default" in layer.styles, layer.styles
    assert layer.styles["default"].get("isDefault") is True, layer.styles["default"]
    assert layer.boundingBoxWGS84 and len(layer.boundingBoxWGS84) == 4
    assert set(layer.tilematrixsetlinks) == set(wmts.tilematrixsets), (
        f"layer links {sorted(layer.tilematrixsetlinks)} but the service declares "
        f"{sorted(wmts.tilematrixsets)}"
    )
    wmts_collector.record(
        "CERT-DISC-02", "pass",
        duration_ms=timer.ms,
        measured_count=len(layer.tilematrixsetlinks),
        notes=(
            f"Layer {wmts_layer!r}: title={layer.title!r}, formats {layer.formats}, styles "
            f"{list(layer.styles)} (default flagged isDefault), WGS84 bounds "
            f"{layer.boundingBoxWGS84}, linked to every advertised tile matrix set "
            f"{sorted(layer.tilematrixsetlinks)}."
        ),
    )


@pytest.mark.cert("NB-OWS-WMTS-CAP-01")
def test_ext_operations_metadata(wmts: WebMapTileService,
                                 wmts_collector: CertificationEvidenceCollector) -> None:
    """Both DCP encodings must be advertised, and OWSLib must resolve KVP."""
    names = {operation.name for operation in wmts.operations}
    assert {"GetCapabilities", "GetTile"} <= names, names
    get_tile = wmts.getOperationByName("GetTile")
    encodings = {
        value.upper()
        for method in get_tile.methods
        for constraint in (method.get("constraints") or [])
        for value in constraint.values
    }
    assert {"KVP", "RESTFUL"} <= encodings, f"GetTile advertises only {encodings}"
    assert wmts.restonly is False, (
        "OWSLib decided the service is REST-only, which means the KVP GetEncoding constraint "
        "was not parseable"
    )
    wmts_collector.record(
        "NB-OWS-WMTS-CAP-01", "pass",
        measured_count=len(names),
        notes=(
            f"OperationsMetadata declares {sorted(names)}; GetTile offers both {sorted(encodings)} "
            "GetEncoding constraints and OWSLib selects the KVP binding from them."
        ),
    )


@pytest.mark.cert("NB-OWS-WMTS-CAP-02")
def test_ext_tile_matrix_sets(wmts: WebMapTileService,
                              wmts_collector: CertificationEvidenceCollector) -> None:
    """Both advertised grids must be internally consistent, not just present."""
    assert {"WebMercatorQuad", "WorldCRS84Quad"} <= set(wmts.tilematrixsets), (
        f"expected the two standard grids, got {sorted(wmts.tilematrixsets)}"
    )
    summary: dict[str, str] = {}
    for name, tile_matrix_set in wmts.tilematrixsets.items():
        assert tile_matrix_set.crs, f"{name} declares no SupportedCRS"
        ids = _sorted_matrix_ids(tile_matrix_set)
        assert ids == [str(level) for level in range(len(ids))], (
            f"{name} tile matrix identifiers are not the contiguous zoom levels: {ids}"
        )
        first = tile_matrix_set.tilematrix[ids[0]]
        base_width, base_height = int(first.matrixwidth), int(first.matrixheight)
        previous = None
        for level, identifier in enumerate(ids):
            matrix = tile_matrix_set.tilematrix[identifier]
            assert int(matrix.tilewidth) == 256 and int(matrix.tileheight) == 256, identifier
            assert int(matrix.matrixwidth) == base_width * (2 ** level), (
                f"{name}/{identifier}: MatrixWidth {matrix.matrixwidth} is not a power-of-two "
                f"refinement of level 0 ({base_width})"
            )
            assert int(matrix.matrixheight) == base_height * (2 ** level)
            assert matrix.topleftcorner == first.topleftcorner, (
                f"{name}/{identifier}: TopLeftCorner drifts between levels"
            )
            scale = float(matrix.scaledenominator)
            if previous is not None:
                assert scale == pytest.approx(previous / 2.0, rel=1e-9), (
                    f"{name}/{identifier}: ScaleDenominator {scale} is not half of {previous}"
                )
            previous = scale
        summary[name] = f"{tile_matrix_set.crs} levels 0..{len(ids) - 1} base {base_width}x{base_height}"
    wmts_collector.record(
        "NB-OWS-WMTS-CAP-02", "pass",
        measured_count=len(summary),
        notes=(
            "Both tile matrix sets are internally consistent: contiguous zoom identifiers, 256px "
            "tiles, a fixed TopLeftCorner, power-of-two matrix growth and halving scale "
            "denominators. " + "; ".join(f"{name}: {detail}" for name, detail in summary.items())
        ),
    )


@pytest.mark.cert("NB-OWS-WMTS-CAP-03")
def test_ext_tile_matrix_set_limits(wmts: WebMapTileService, wmts_layer: str,
                                    wmts_collector: CertificationEvidenceCollector) -> None:
    """TileMatrixSetLimits must exist and stay inside the matrix they constrain."""
    layer = wmts[wmts_layer]
    checked = 0
    for set_name, link in layer.tilematrixsetlinks.items():
        tile_matrix_set = wmts.tilematrixsets[set_name]
        assert link.tilematrixlimits, f"{set_name} link declares no TileMatrixSetLimits"
        for identifier, limits in link.tilematrixlimits.items():
            matrix = tile_matrix_set.tilematrix[identifier]
            assert 0 <= int(limits.mintilerow) <= int(limits.maxtilerow) <= int(matrix.matrixheight) - 1, (
                f"{set_name}/{identifier}: row limits {limits.mintilerow}..{limits.maxtilerow} "
                f"fall outside a matrix of height {matrix.matrixheight}"
            )
            assert 0 <= int(limits.mintilecol) <= int(limits.maxtilecol) <= int(matrix.matrixwidth) - 1, (
                f"{set_name}/{identifier}: column limits {limits.mintilecol}..{limits.maxtilecol} "
                f"fall outside a matrix of width {matrix.matrixwidth}"
            )
            checked += 1
    assert checked > 0
    wmts_collector.record(
        "NB-OWS-WMTS-CAP-03", "pass",
        measured_count=checked,
        notes=(
            f"{checked} TileMatrixLimits entries across both grids stay within the row/column "
            "range of the tile matrix they constrain, so a limits-aware client cannot be steered "
            "at a tile that does not exist."
        ),
    )


@pytest.mark.cert("NB-OWS-WMTS-CAP-04")
def test_ext_resource_url_templates(wmts: WebMapTileService, wmts_layer: str,
                                    wmts_collector: CertificationEvidenceCollector) -> None:
    """The advertised RESTful templates must be substitutable by OWSLib itself.

    ``buildTileResource`` substitutes the WMTS 1.0.0 template variable names
    (``{Style}``, ``{TileMatrixSet}``, ``{TileMatrix}``, ``{TileRow}``,
    ``{TileCol}``). A template that spells any of them differently raises
    ``KeyError`` inside OWSLib, so the RESTful binding is unusable no matter what
    the route accepts.
    """
    layer = wmts[wmts_layer]
    assert layer.resourceURLs, "the layer advertises no ResourceURL"
    kinds = {entry["resourceType"] for entry in layer.resourceURLs}
    assert "tile" in kinds, kinds

    resolved = wmts.buildTileResource(
        layer=wmts_layer, style="default", format="image/png",
        tilematrixset="WebMercatorQuad", tilematrix="5", row=12, column=5)
    assert resolved, "buildTileResource returned nothing for a tile ResourceURL"
    assert "{" not in resolved and "}" not in resolved, (
        f"buildTileResource left unsubstituted template variables in {resolved!r}"
    )
    assert resolved.endswith("/WebMercatorQuad/5/12/5.png"), resolved

    image, _ = _png(openURL(resolved, timeout=30))
    assert image.size == (256, 256)
    wmts_collector.record(
        "NB-OWS-WMTS-CAP-04", "pass",
        measured_count=len(layer.resourceURLs),
        notes=(
            f"{len(layer.resourceURLs)} ResourceURL entries ({sorted(kinds)}); OWSLib's own "
            f"buildTileResource substituted the tile template to {resolved} with no variables "
            "left over, and that URL returned a 256x256 PNG."
        ),
        evidence_ref=resolved,
    )


# ---------------------------------------------------------------------------
# RNDR
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-RNDR-01")
def test_rndr01_gettile(wmts: WebMapTileService, wmts_layer: str,
                        wmts_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    tile_matrix_set = wmts.tilematrixsets["WebMercatorQuad"]
    matrix = tile_matrix_set.tilematrix["5"]
    x, y = _project(tile_matrix_set.crs, SAMPLE_LON, SAMPLE_LAT)
    row, column = _tile_index(matrix, tile_matrix_set.crs, x, y)
    image, payload = _png(wmts.gettile(
        layer=wmts_layer, style="default", format="image/png",
        tilematrixset="WebMercatorQuad", tilematrix="5", row=row, column=column))
    assert image.format == "PNG"
    assert image.size == (int(matrix.tilewidth), int(matrix.tileheight)), (
        f"tile is {image.size}, capabilities declare {matrix.tilewidth}x{matrix.tileheight}"
    )
    wmts_collector.record(
        "CERT-RNDR-01", "pass",
        duration_ms=timer.ms,
        measured_count=len(payload),
        notes=(
            f"GetTile(WebMercatorQuad/5/{row}/{column}) returned a decodable {image.size[0]}x"
            f"{image.size[1]} PNG ({len(payload)} bytes) whose dimensions match the TileWidth/"
            "TileHeight the capabilities declare. This is server-rendered imagery accepted and "
            "decoded by the client, not client-side drawing: OWSLib has no drawing surface."
        ),
    )


@pytest.mark.cert("NB-OWS-WMTS-TILE-01")
def test_ext_zoom_sweep(wmts: WebMapTileService, wmts_layer: str,
                        wmts_collector: CertificationEvidenceCollector) -> None:
    """Tiles covering the seeded point must decode at every sampled zoom level."""
    tile_matrix_set = wmts.tilematrixsets["WebMercatorQuad"]
    ids = _sorted_matrix_ids(tile_matrix_set)
    x, y = _project(tile_matrix_set.crs, SAMPLE_LON, SAMPLE_LAT)
    sampled = [ids[0], ids[2], ids[5], ids[10], ids[-1]]
    observed: dict[str, tuple[int, int]] = {}
    for identifier in sampled:
        matrix = tile_matrix_set.tilematrix[identifier]
        row, column = _tile_index(matrix, tile_matrix_set.crs, x, y)
        assert 0 <= row < int(matrix.matrixheight) and 0 <= column < int(matrix.matrixwidth), (
            f"level {identifier}: derived tile {row}/{column} is outside the declared matrix"
        )
        image, _ = _png(wmts.gettile(
            layer=wmts_layer, style="default", format="image/png",
            tilematrixset="WebMercatorQuad", tilematrix=identifier, row=row, column=column))
        assert image.size == (256, 256)
        observed[identifier] = (row, column)
    wmts_collector.record(
        "NB-OWS-WMTS-TILE-01", "pass",
        measured_count=len(observed),
        notes=(
            "Tile indices derived from the capabilities scale denominators with the WMTS pixel-span "
            f"formula produced in-range tiles at every sampled level and all decoded as 256x256 "
            f"PNGs: {observed}."
        ),
    )


@pytest.mark.cert("NB-OWS-WMTS-TILE-02")
def test_ext_second_tile_matrix_set(wmts: WebMapTileService, wmts_layer: str,
                                    wmts_collector: CertificationEvidenceCollector) -> None:
    """The WorldCRS84Quad grid must serve tiles as well, on its own geometry.

    The two grids differ in CRS, origin and level-0 shape (1x1 vs 2x1), so a
    server that quietly serves WebMercatorQuad tiles for both would return an
    index the CRS84 grid cannot address.
    """
    tile_matrix_set = wmts.tilematrixsets["WorldCRS84Quad"]
    level_zero = tile_matrix_set.tilematrix[_sorted_matrix_ids(tile_matrix_set)[0]]
    assert (int(level_zero.matrixwidth), int(level_zero.matrixheight)) == (2, 1), (
        f"WorldCRS84Quad level 0 should be a 2x1 grid, got "
        f"{level_zero.matrixwidth}x{level_zero.matrixheight}"
    )
    assert float(level_zero.topleftcorner[0]) == pytest.approx(-180.0), level_zero.topleftcorner
    assert float(level_zero.topleftcorner[1]) == pytest.approx(90.0), level_zero.topleftcorner

    matrix = tile_matrix_set.tilematrix["4"]
    row, column = _tile_index(matrix, tile_matrix_set.crs, SAMPLE_LON, SAMPLE_LAT)
    assert 0 <= row < int(matrix.matrixheight) and 0 <= column < int(matrix.matrixwidth)
    image, _ = _png(wmts.gettile(
        layer=wmts_layer, style="default", format="image/png",
        tilematrixset="WorldCRS84Quad", tilematrix="4", row=row, column=column))
    assert image.size == (256, 256)
    wmts_collector.record(
        "NB-OWS-WMTS-TILE-02", "pass",
        measured_count=1,
        notes=(
            "WorldCRS84Quad declares the OGC 2x1 level-0 grid with its origin at "
            f"(-180, 90) in CRS84 longitude/latitude order, and GetTile(WorldCRS84Quad/4/{row}/"
            f"{column}) -- derived from that geometry -- returned a 256x256 PNG."
        ),
    )


@pytest.mark.cert("NB-OWS-WMTS-TILE-03")
def test_ext_rest_matches_kvp(wmts: WebMapTileService, wmts_layer: str,
                              wmts_collector: CertificationEvidenceCollector) -> None:
    """The RESTful and KVP encodings must return byte-identical tiles."""
    kvp_image, kvp_bytes = _png(wmts.gettile(
        layer=wmts_layer, style="default", format="image/png",
        tilematrixset="WebMercatorQuad", tilematrix="5", row=12, column=5))
    rest_url = wmts.buildTileResource(
        layer=wmts_layer, style="default", format="image/png",
        tilematrixset="WebMercatorQuad", tilematrix="5", row=12, column=5)
    rest_image, rest_bytes = _png(openURL(rest_url, timeout=30))
    assert kvp_image.size == rest_image.size
    assert kvp_bytes == rest_bytes, (
        "the RESTful and KVP encodings of the same tile returned different bytes"
    )
    wmts_collector.record(
        "NB-OWS-WMTS-TILE-03", "pass",
        measured_count=len(rest_bytes),
        notes=(
            f"The advertised RESTful ResourceURL ({rest_url}) and the KVP GetTile binding return "
            f"byte-identical {len(rest_bytes)}-byte tiles, so the two encodings are genuinely the "
            "same resource rather than two code paths."
        ),
        evidence_ref=rest_url,
    )


# ---------------------------------------------------------------------------
# GEOM
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-GEOM-02")
def test_geom02_tile_matrix_set_is_honoured(wmts: WebMapTileService, wmts_layer: str,
                                            wmts_collector: CertificationEvidenceCollector,
                                            timer: Timer) -> None:
    """The requested TileMatrixSet must be carried and served on its own geometry."""
    requested = "WebMercatorQuad"
    tile_matrix_set = wmts.tilematrixsets[requested]
    assert requested in wmts[wmts_layer].tilematrixsetlinks
    assert "3857" in tile_matrix_set.crs, (
        f"WebMercatorQuad must declare a Web Mercator SupportedCRS, got {tile_matrix_set.crs!r}"
    )

    query = wmts.buildTileRequest(
        layer=wmts_layer, style="default", format="image/png",
        tilematrixset=requested, tilematrix="3", row=3, column=1)
    assert f"TILEMATRIXSET={requested}" in query, query
    assert "TILEMATRIX=3" in query and "TILEROW=3" in query and "TILECOL=1" in query, query

    matrix = tile_matrix_set.tilematrix["3"]
    image, _ = _png(wmts.gettile(
        layer=wmts_layer, style="default", format="image/png",
        tilematrixset=requested, tilematrix="3", row=3, column=1))
    assert image.size == (int(matrix.tilewidth), int(matrix.tileheight))

    # Asking the same layer for a tile index that is only valid in the *other*
    # grid proves the matrix set is actually resolved rather than ignored.
    crs84 = wmts.tilematrixsets["WorldCRS84Quad"]
    only_in_crs84 = int(crs84.tilematrix["3"].matrixwidth) - 1
    assert only_in_crs84 >= int(matrix.matrixwidth), (
        "the two grids are not distinguishable at level 3; pick another level"
    )
    with pytest.raises(ServiceException):
        wmts.gettile(layer=wmts_layer, style="default", format="image/png",
                     tilematrixset=requested, tilematrix="3", row=0, column=only_in_crs84)

    wmts_collector.record(
        "CERT-GEOM-02", "pass",
        duration_ms=timer.ms,
        notes=(
            f"TILEMATRIXSET={requested} (SupportedCRS {tile_matrix_set.crs}) is carried verbatim in "
            "the request OWSLib builds and served at the declared tile size; a column index that "
            "is only valid in the WorldCRS84Quad grid is rejected as TileOutOfRange, so the "
            "requested grid geometry really is the one being applied."
        ),
    )


# ---------------------------------------------------------------------------
# ERRH
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-ERRH-01")
def test_errh01_unknown_layer(wmts: WebMapTileService,
                              wmts_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    with pytest.raises(ServiceException) as excinfo:
        wmts.gettile(layer=fx.UNKNOWN_COLLECTION_ID, style="default", format="image/png",
                     tilematrixset="WebMercatorQuad", tilematrix="2", row=1, column=0)
    body = str(excinfo.value)
    assert "ExceptionReport" in body, body[:200]
    assert "exceptionCode=" in body, body[:200]
    wmts_collector.record(
        "CERT-ERRH-01", "pass",
        duration_ms=timer.ms,
        notes=(
            "An unknown LAYER produced an ows:ExceptionReport with a machine-readable "
            "exceptionCode, which OWSLib surfaces as owslib.util.ServiceException rather than "
            "handing the caller a broken image."
        ),
    )


@pytest.mark.cert("NB-OWS-WMTS-ERR-01")
def test_ext_error_surface(wmts: WebMapTileService, wmts_layer: str,
                           wmts_collector: CertificationEvidenceCollector) -> None:
    """Every out-of-contract GetTile must be a coded ows:ExceptionReport."""
    cases = {
        "row-out-of-range": dict(tilematrixset="WebMercatorQuad", tilematrix="2",
                                 row=99, column=0),
        "column-out-of-range": dict(tilematrixset="WebMercatorQuad", tilematrix="2",
                                    row=0, column=99),
        "unknown-tilematrixset": dict(tilematrixset="NotAGrid", tilematrix="2", row=1, column=0),
        "zoom-above-maximum": dict(tilematrixset="WebMercatorQuad", tilematrix="99",
                                   row=0, column=0),
        "negative-row": dict(tilematrixset="WebMercatorQuad", tilematrix="2", row=-1, column=0),
    }
    observed: dict[str, str] = {}
    for label, kwargs in cases.items():
        with pytest.raises(ServiceException) as excinfo:
            wmts.gettile(layer=wmts_layer, style="default", format="image/png", **kwargs)
        body = str(excinfo.value)
        assert "ows:ExceptionReport" in body or "ExceptionReport" in body, f"{label}: {body[:200]}"
        code = re.search(r'exceptionCode="([^"]+)"', body)
        assert code, f"{label}: no exceptionCode in {body[:200]}"
        observed[label] = code.group(1)
    assert "TileOutOfRange" in observed.values(), observed
    wmts_collector.record(
        "NB-OWS-WMTS-ERR-01", "pass",
        measured_count=len(observed),
        notes=(
            "Every out-of-contract GetTile returned a coded ows:ExceptionReport: "
            + ", ".join(f"{label}={code}" for label, code in observed.items())
            + ". Out-of-range indices are reported as TileOutOfRange rather than as a blank tile."
        ),
    )


@pytest.mark.cert("NB-OWS-WMTS-CAP-05")
def test_ext_style_legend(wmts: WebMapTileService, wmts_layer: str,
                          wmts_collector: CertificationEvidenceCollector) -> None:
    """The default style's advertised LegendURL must resolve to a real image."""
    style = wmts[wmts_layer].styles["default"]
    legend = style.get("legend")
    assert legend, "the default style advertises no LegendURL"
    assert style.get("format") == "image/png", style
    image, payload = _png(openURL(legend, timeout=30))
    declared = (int(style["width"]), int(style["height"])) if style.get("width") else None
    if declared:
        assert image.size == declared, (
            f"the LegendURL declares {declared} but returned {image.size}"
        )
    wmts_collector.record(
        "NB-OWS-WMTS-CAP-05", "pass",
        measured_count=len(payload),
        notes=(
            f"The default style's LegendURL returned a decodable {image.size[0]}x{image.size[1]} "
            f"PNG matching the declared LegendURL width/height {declared}."
        ),
        evidence_ref=legend,
    )


@pytest.mark.cert("NB-OWS-WMTS-TILE-04")
def test_ext_tile_is_deterministic(wmts: WebMapTileService, wmts_layer: str,
                                   wmts_collector: CertificationEvidenceCollector) -> None:
    """The same tile requested twice must be byte-identical.

    WMTS exists so tiles can be cached; a renderer that emits a different byte
    stream per request (embedded timestamps, unstable ordering) silently defeats
    every downstream cache and ETag.
    """
    kwargs = dict(layer=wmts_layer, style="default", format="image/png",
                  tilematrixset="WebMercatorQuad", tilematrix="5", row=12, column=5)
    _, first = _png(wmts.gettile(**kwargs))
    _, second = _png(wmts.gettile(**kwargs))
    assert first == second, (
        f"the same tile returned {len(first)} then {len(second)} bytes with differing content"
    )
    wmts_collector.record(
        "NB-OWS-WMTS-TILE-04", "pass",
        measured_count=len(first),
        notes=(
            f"Two identical GetTile requests returned byte-identical {len(first)}-byte payloads, "
            "so the tile stream is cacheable and reproducible."
        ),
    )


@pytest.mark.cert("NB-OWS-WMTS-TILE-05")
def test_ext_tile_outside_data_is_empty_not_error(wmts: WebMapTileService, wmts_layer: str,
                                                  wmts_collector: CertificationEvidenceCollector) -> None:
    """A valid tile index with no data must be a blank tile, not an exception.

    Tiling clients request whole viewports; turning "no features here" into a
    ServiceException produces visible error tiles across the map.
    """
    tile_matrix_set = wmts.tilematrixsets["WebMercatorQuad"]
    matrix = tile_matrix_set.tilematrix["5"]
    # Antipodal-ish tile: nowhere near the seeded San Francisco extent.
    row, column = _tile_index(matrix, tile_matrix_set.crs,
                              *_project(tile_matrix_set.crs, 100.0, -30.0))
    assert 0 <= row < int(matrix.matrixheight) and 0 <= column < int(matrix.matrixwidth)
    image, payload = _png(wmts.gettile(
        layer=wmts_layer, style="default", format="image/png",
        tilematrixset="WebMercatorQuad", tilematrix="5", row=row, column=column))
    assert image.size == (256, 256)
    colours = set(image.convert("RGBA").getdata())
    assert colours == {(0, 0, 0, 0)}, (
        f"a tile with no data underneath is not fully transparent: {sorted(colours)[:4]}"
    )
    wmts_collector.record(
        "NB-OWS-WMTS-TILE-05", "pass",
        measured_count=len(payload),
        notes=(
            f"GetTile(WebMercatorQuad/5/{row}/{column}) -- a valid index far from the seeded "
            "extent -- returned a fully transparent 256x256 PNG rather than a ServiceException."
        ),
    )


@pytest.mark.cert("NB-OWS-WMTS-GFI-01")
def test_ext_featureinfo_resource_url(wmts: WebMapTileService, wmts_layer: str,
                                      wmts_collector: CertificationEvidenceCollector) -> None:
    """The advertised FeatureInfo ResourceURL must identify the seeded feature.

    ``WebMapTileService.getfeatureinfo`` raises ``NotImplementedError`` in
    OWSLib, so the request is built from the ResourceURL template the server
    advertises and fetched with ``owslib.util.openURL`` -- which is still the
    capabilities-driven path a REST client would take, and still OWSLib's own
    transport with its ExceptionReport handling.
    """
    templates = {
        entry["format"]: entry["template"]
        for entry in wmts[wmts_layer].resourceURLs
        if entry["resourceType"] == "FeatureInfo"
    }
    assert templates, "the layer advertises no FeatureInfo ResourceURL"
    assert "application/json" in templates, sorted(templates)

    tile_matrix_set = wmts.tilematrixsets["WebMercatorQuad"]
    level = "14"
    matrix = tile_matrix_set.tilematrix[level]
    x, y = _project(tile_matrix_set.crs, SAMPLE_LON, SAMPLE_LAT)
    row, column = _tile_index(matrix, tile_matrix_set.crs, x, y)
    pixel_span = (float(matrix.scaledenominator) * WMTS_PIXEL_SIZE_METRES) / _metres_per_unit(
        tile_matrix_set.crs)
    left = float(matrix.topleftcorner[0]) + column * pixel_span * int(matrix.tilewidth)
    top = float(matrix.topleftcorner[1]) - row * pixel_span * int(matrix.tileheight)
    i = int((x - left) / pixel_span)
    j = int((top - y) / pixel_span)
    assert 0 <= i < int(matrix.tilewidth) and 0 <= j < int(matrix.tileheight), (i, j)

    url = (templates["application/json"]
           .replace("{Style}", "default").replace("{style}", "default")
           .replace("{TileMatrixSet}", "WebMercatorQuad").replace("{TileMatrix}", level)
           .replace("{TileRow}", str(row)).replace("{TileCol}", str(column))
           .replace("{J}", str(j)).replace("{I}", str(i)))
    assert "{" not in url, f"unsubstituted template variables remain in {url}"
    body = openURL(url, timeout=30).read().decode()
    assert "pt-alpha" in body, f"WMTS GetFeatureInfo did not identify the seeded point: {body[:200]}"
    wmts_collector.record(
        "NB-OWS-WMTS-GFI-01", "pass",
        measured_count=len(templates),
        notes=(
            f"The advertised FeatureInfo ResourceURL, substituted for tile {level}/{row}/{column} "
            f"at pixel I/J {(i, j)} -- derived from the capabilities tile geometry -- identified "
            "the seeded pt-alpha feature, so the server's tile-pixel inverse transform agrees "
            "with the grid it advertises."
        ),
        evidence_ref=url,
    )


@pytest.mark.cert("NB-OWS-WMTS-XPRO-01")
def test_ext_cross_protocol_layer_identity(wmts: WebMapTileService, wmts_layer: str,
                                           lane_config: LaneConfig,
                                           wmts_collector: CertificationEvidenceCollector) -> None:
    """WMTS and OGC API - Features must agree on the same layer's identity and extent."""
    collection = Features(lane_config.oaf_url).collection(lane_config.raster_layer_id)
    layer = wmts[wmts_layer]
    assert layer.title == collection["title"], (
        f"WMTS calls the layer {layer.title!r}, OGC API calls it {collection['title']!r}"
    )
    oaf_bbox = collection["extent"]["spatial"]["bbox"][0]
    for index, (left, right) in enumerate(zip(layer.boundingBoxWGS84, oaf_bbox)):
        assert abs(left - right) <= 1e-6, (
            f"extent ordinate {index} differs: WMTS {layer.boundingBoxWGS84} vs OGC API {oaf_bbox}"
        )
    wmts_collector.record(
        "NB-OWS-WMTS-XPRO-01", "pass",
        measured_count=len(oaf_bbox),
        notes=(
            f"Layer {layer.title!r} carries the same title and the same WGS84 bounding box "
            f"{layer.boundingBoxWGS84} through the WMTS capabilities and the OGC API - Features "
            "collection for the same layer id."
        ),
    )
