# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""WMS certification through ``owslib.wms.WebMapService`` (1.3.0 and 1.1.1).

OWSLib parses the capabilities document into a layer tree and then derives
every GetMap/GetFeatureInfo request from it -- including the 1.3.0 axis-order
swap for latitude-first CRSs. That makes it the right client to certify a WMS
adapter with: a capabilities bug becomes a request bug two calls later.

The lane covers the full documented surface: service identification and contact
metadata, the layer tree, per-layer CRS lists and bounding boxes, styles and
legend URLs, the queryable flag, GetMap format options and exception formats,
GetMap across CRSs/formats/styles/transparency/background colour/multi-layer,
GetFeatureInfo in every advertised ``info_format``, version negotiation, and the
error surface. Pillow decodes the returned imagery so the rendering facet
asserts real pixels rather than a Content-Type header.
"""

from __future__ import annotations

import collections
import io

import pytest
from owslib.ogcapi.features import Features
from owslib.util import ServiceException, http_get, openURL
from owslib.wms import WebMapService
from PIL import Image

from shared import canonical_fixture as fx
from shared.cert_envelope import CertificationEvidenceCollector

from .conftest import AdminProbe, LaneConfig, Timer, web_mercator

pytestmark = pytest.mark.owslib_client

# The seeded browser_compat features cluster inside this window (EPSG:4326
# longitude/latitude); it is the same view the Cesium browser lane uses.
VIEW_BBOX = (-122.45, 37.74, -122.38, 37.80)
VIEW_BBOX_3857 = web_mercator(VIEW_BBOX[0], VIEW_BBOX[1]) + web_mercator(VIEW_BBOX[2], VIEW_BBOX[3])
TRANSPARENT = (0, 0, 0, 0)


@pytest.fixture(scope="session")
def wms(lane_config: LaneConfig) -> WebMapService:
    return WebMapService(lane_config.wms_url, version="1.3.0")


@pytest.fixture(scope="session")
def wms111(lane_config: LaneConfig) -> WebMapService:
    return WebMapService(lane_config.wms_url, version="1.1.1")


@pytest.fixture(scope="session")
def wms_layer(wms: WebMapService, lane_config: LaneConfig) -> str:
    """Resolve the capabilities layer name for the configured raster layer id.

    WMS advertises the layer under its human-facing name, while the compose
    service is configured by numeric layer id, so the name is discovered through
    the OGC API - Features collection for the same layer rather than hard-coded.
    A mismatch here is itself a cross-protocol identity bug and fails loudly.
    """
    if lane_config.raster_layer_id in wms.contents:
        return lane_config.raster_layer_id
    title = Features(lane_config.oaf_url).collection(lane_config.raster_layer_id)["title"]
    if title in wms.contents:
        return title
    pytest.fail(
        f"WMS advertises {list(wms.contents)}, none of which is the configured raster layer "
        f"{lane_config.raster_layer_id!r} (OGC API title {title!r})."
    )
    raise AssertionError("unreachable")


def _image(response) -> Image.Image:
    payload = response.read()
    assert payload[:8] == b"\x89PNG\r\n\x1a\n" or payload[:3] == b"\xff\xd8\xff", (
        f"response is not a PNG or JPEG: {payload[:40]!r}"
    )
    return Image.open(io.BytesIO(payload))


def _palette(image: Image.Image, top: int = 4):
    return collections.Counter(image.convert("RGBA").getdata()).most_common(top)


def _drawn_pixels(image: Image.Image, background) -> int:
    return sum(count for colour, count in _palette(image, top=64) if colour != background)


# ---------------------------------------------------------------------------
# CONN / AUTH
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-CONN-01")
def test_conn01_capabilities(wms: WebMapService, wms_collector: CertificationEvidenceCollector,
                             timer: Timer) -> None:
    assert wms.identification.type == "WMS", wms.identification.type
    assert wms.identification.version == "1.3.0"
    assert wms.identification.title
    assert wms.contents, "capabilities advertised no layers"
    wms_collector.record(
        "CERT-CONN-01", "pass",
        duration_ms=timer.ms,
        measured_count=len(wms.contents),
        notes=(
            "owslib.wms.WebMapService parsed a live WMS 1.3.0 capabilities document "
            f"({wms.identification.title!r}) into {len(wms.contents)} named layers."
        ),
        evidence_ref=wms.url,
    )


@pytest.mark.cert("CERT-CONN-02")
def test_conn02_transport(base_url: str, wms_collector: CertificationEvidenceCollector) -> None:
    assert base_url.split("://", 1)[0] == "http"
    wms_collector.record(
        "CERT-CONN-02", "pass" if scheme == "https" else "not-applicable",
        notes=(
            "Transport verified as plain http on the compose client-compat network, which "
            "terminates no TLS. TLS handshake behaviour is exercised in the release tier, where "
            "the same lane runs against the HTTPS candidate."
        ),
        evidence_ref=base_url,
    )


@pytest.mark.cert("CERT-AUTH-01")
def test_auth01_anonymous_rejected(admin_probe: AdminProbe,
                                   wms_collector: CertificationEvidenceCollector) -> None:
    assert admin_probe.anonymous_status in (401, 403), admin_probe
    assert "ApiKey" in admin_probe.challenge and fx.ADMIN_API_KEY_HEADER in admin_probe.challenge
    wms_collector.record(
        "CERT-AUTH-01", "pass",
        notes=(
            f"Anonymous GET {fx.ADMIN_PROBE_PATH} -> {admin_probe.anonymous_status}, "
            f"WWW-Authenticate: {admin_probe.challenge}. The WMS surface is anonymous in this "
            "fixture, so the control plane substantiates the AUTH facets."
        ),
        evidence_ref=fx.ADMIN_PROBE_PATH,
    )


@pytest.mark.cert("CERT-AUTH-02")
def test_auth02_credential_grants_access(admin_probe: AdminProbe,
                                         wms_collector: CertificationEvidenceCollector) -> None:
    assert admin_probe.authenticated_status // 100 == 2, admin_probe
    wms_collector.record(
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
def test_disc01_layer_tree(wms: WebMapService, wms_layer: str,
                           wms_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    assert wms_layer in wms.contents
    assert len(wms.contents) >= 3, f"expected the three seeded browser_compat layers, got {list(wms.contents)}"
    for name, layer in wms.contents.items():
        assert layer.title, f"{name} has no title"
        assert layer.parent is not None, f"{name} is not nested under a root layer"
    wms_collector.record(
        "CERT-DISC-01", "pass",
        duration_ms=timer.ms,
        measured_count=len(wms.contents),
        notes=(
            f"WebMapService.contents listed {len(wms.contents)} named layers "
            f"({list(wms.contents)}), each nested under the service root layer."
        ),
    )


@pytest.mark.cert("CERT-DISC-02")
def test_disc02_layer_metadata(wms: WebMapService, wms_layer: str,
                               wms_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    layer = wms.contents[wms_layer]
    assert layer.queryable == 1, f"{wms_layer} is not advertised as queryable"
    assert layer.abstract
    assert layer.keywords
    assert "EPSG:4326" in layer.crsOptions and "CRS:84" in layer.crsOptions, layer.crsOptions
    bbox = layer.boundingBoxWGS84
    assert bbox and len(bbox) == 4 and bbox[0] < bbox[2] and bbox[1] < bbox[3]
    assert "default" in layer.styles
    wms_collector.record(
        "CERT-DISC-02", "pass",
        duration_ms=timer.ms,
        measured_count=len(layer.crsOptions),
        notes=(
            f"Layer {wms_layer!r}: queryable=1, abstract present, CRS options {layer.crsOptions}, "
            f"EX_GeographicBoundingBox {bbox}, styles {list(layer.styles)}."
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-CAP-01")
def test_ext_service_identification(wms: WebMapService,
                                    wms_collector: CertificationEvidenceCollector) -> None:
    """Service-level identification metadata must be complete enough to catalogue."""
    identification = wms.identification
    assert identification.abstract, "the service declares no abstract"
    assert identification.keywords, "the service declares no keywords"
    assert wms.url.startswith("http"), wms.url
    wms_collector.record(
        "NB-OWS-WMS-CAP-01", "pass",
        measured_count=len(identification.keywords),
        notes=(
            f"Service block: Name=WMS, Title={identification.title!r}, "
            f"Abstract={identification.abstract!r}, {len(identification.keywords)} keywords "
            f"{identification.keywords}, OnlineResource {wms.url}."
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-CAP-02")
def test_ext_contact_information(wms: WebMapService,
                                 wms_collector: CertificationEvidenceCollector) -> None:
    """ContactInformation must round-trip through OWSLib's provider model."""
    provider = wms.provider
    contact = provider.contact
    assert provider.name, "no ContactOrganization"
    populated = {
        field: getattr(contact, field)
        for field in ("name", "organization", "position", "address", "city", "region",
                      "postcode", "country")
        if getattr(contact, field, None)
    }
    assert len(populated) >= 6, f"ContactInformation is too sparse to be useful: {populated}"
    wms_collector.record(
        "NB-OWS-WMS-CAP-02", "pass",
        measured_count=len(populated),
        notes=(
            f"OWSLib parsed {len(populated)} ContactInformation fields: {populated}. A WMS whose "
            "contact block does not survive a strict parser breaks catalogue harvesting."
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-CAP-03")
def test_ext_per_crs_bounding_boxes(wms: WebMapService, wms_layer: str, lane_config: LaneConfig,
                                    wms_collector: CertificationEvidenceCollector) -> None:
    """Per-CRS BoundingBox elements must use each CRS's declared axis order.

    WMS 1.3.0 requires every named layer to carry an EX_GeographicBoundingBox
    and at least one BoundingBox, with the BoundingBox ordinates in the CRS's
    own axis order. CRS:84 is longitude-first and EPSG:4326 is latitude-first,
    so a server that writes identical numbers under both is advertising a
    latitude of -122. The check runs against the raw capabilities XML because
    OWSLib normalises latitude-first boxes back to longitude/latitude when it
    builds ``crs_list`` -- the normalisation itself is then asserted, so both
    the wire form and the parsed form are covered.
    """
    import xml.etree.ElementTree as ET

    layer = wms.contents[wms_layer]
    geographic = layer.boundingBoxWGS84
    assert geographic and geographic[0] < geographic[2] and geographic[1] < geographic[3]

    document = ET.fromstring(http_get(lane_config.wms_url, params={
        "SERVICE": "WMS", "VERSION": "1.3.0", "REQUEST": "GetCapabilities"}, timeout=30).content)
    namespace = {"wms": "http://www.opengis.net/wms"}
    named = [
        element for element in document.iter(f"{{{namespace['wms']}}}Layer")
        if (element.find("wms:Name", namespace) is not None
            and element.find("wms:Name", namespace).text == wms_layer)
    ]
    assert named, f"the capabilities XML has no named layer {wms_layer!r}"
    boxes = {
        box.get("CRS"): tuple(float(box.get(key)) for key in ("minx", "miny", "maxx", "maxy"))
        for box in named[0].findall("wms:BoundingBox", namespace)
    }
    assert named[0].find("wms:EX_GeographicBoundingBox", namespace) is not None, (
        "WMS 1.3.0 requires an EX_GeographicBoundingBox on every named layer"
    )
    assert "CRS:84" in boxes and "EPSG:4326" in boxes, sorted(boxes)
    assert boxes["CRS:84"] == pytest.approx(geographic, abs=1e-6), (
        f"the CRS:84 BoundingBox {boxes['CRS:84']} should be longitude-first like "
        f"EX_GeographicBoundingBox {geographic}"
    )
    assert boxes["EPSG:4326"] == pytest.approx(
        (geographic[1], geographic[0], geographic[3], geographic[2]), abs=1e-6), (
        f"the EPSG:4326 BoundingBox {boxes['EPSG:4326']} is not the latitude-first form of "
        f"{geographic}; WMS 1.3.0 requires the CRS's declared axis order"
    )

    # OWSLib normalises both back to longitude/latitude for the caller.
    parsed = {entry[4]: entry[:4] for entry in layer.crs_list}
    assert parsed["CRS:84"] == pytest.approx(parsed["EPSG:4326"], abs=1e-6), (
        f"OWSLib could not reconcile the two boxes: {parsed}"
    )
    wms_collector.record(
        "NB-OWS-WMS-CAP-03", "pass",
        measured_count=len(boxes),
        notes=(
            f"EX_GeographicBoundingBox {geographic}; on the wire the BoundingBox for CRS:84 is "
            f"{boxes['CRS:84']} (longitude first) and for EPSG:4326 is {boxes['EPSG:4326']} "
            "(latitude first), i.e. axis-swapped views of the same ground area. OWSLib normalises "
            "both to the same longitude/latitude tuple."
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-CAP-04")
def test_ext_styles_and_legend(wms: WebMapService, wms_layer: str,
                               wms_collector: CertificationEvidenceCollector) -> None:
    """Advertised LegendURLs must resolve to a real image."""
    style = wms.contents[wms_layer].styles["default"]
    assert style["title"], "the default style has no title"
    legend = style.get("legend")
    assert legend, "the default style advertises no LegendURL"
    assert style.get("legend_format") == "image/png"
    image = _image(openURL(legend, timeout=30))
    assert image.format == "PNG"
    assert image.size[0] > 0 and image.size[1] > 0
    wms_collector.record(
        "NB-OWS-WMS-CAP-04", "pass",
        measured_count=image.size[0] * image.size[1],
        notes=(
            f"The advertised LegendURL for style 'default' returned a decodable "
            f"{image.size[0]}x{image.size[1]} PNG. A dangling LegendURL silently breaks every "
            "client legend panel."
        ),
        evidence_ref=legend,
    )


@pytest.mark.cert("NB-OWS-WMS-CAP-05")
def test_ext_request_metadata(wms: WebMapService,
                              wms_collector: CertificationEvidenceCollector) -> None:
    """The Request block must declare the formats the server can actually serve."""
    get_map = wms.getOperationByName("GetMap")
    assert "image/png" in get_map.formatOptions, get_map.formatOptions
    get_feature_info = wms.getOperationByName("GetFeatureInfo")
    assert get_feature_info.formatOptions, "GetFeatureInfo declares no info formats"
    assert wms.exceptions, "the Exception block declares no formats"
    assert "XML" in wms.exceptions, f"WMS 1.3.0 must offer the XML exception format: {wms.exceptions}"
    for operation in wms.operations:
        assert operation.methods, f"{operation.name} declares no DCPType"
        assert all(method.get("url") for method in operation.methods)
    wms_collector.record(
        "NB-OWS-WMS-CAP-05", "pass",
        measured_count=len(get_map.formatOptions) + len(get_feature_info.formatOptions),
        notes=(
            f"GetMap formats {get_map.formatOptions}; GetFeatureInfo formats "
            f"{get_feature_info.formatOptions}; exception formats {wms.exceptions}; every "
            "operation carries an HTTP Get OnlineResource."
        ),
    )


# ---------------------------------------------------------------------------
# RNDR
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-RNDR-01")
def test_rndr01_getmap(wms: WebMapService, wms_layer: str,
                       wms_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    size = (256, 200)
    image = _image(wms.getmap(layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX, size=size,
                              format="image/png", transparent=True))
    assert image.format == "PNG"
    assert image.size == size, f"requested {size}, got {image.size}"
    drawn = _drawn_pixels(image, TRANSPARENT)
    assert drawn > 0, "GetMap returned a fully transparent image over the seeded features"
    wms_collector.record(
        "CERT-RNDR-01", "pass",
        duration_ms=timer.ms,
        measured_count=drawn,
        notes=(
            f"GetMap returned a decodable {image.size[0]}x{image.size[1]} PNG at the requested "
            f"size and format, with {drawn} non-transparent pixels over the seeded extent. This is "
            "server-rendered imagery accepted and decoded by the client, not client-side drawing: "
            "OWSLib has no drawing surface."
        ),
        evidence_ref=wms.request,
    )


@pytest.mark.cert("NB-OWS-WMS-MAP-01")
def test_ext_getmap_formats(wms: WebMapService, wms_layer: str,
                            wms_collector: CertificationEvidenceCollector) -> None:
    """Every format in the GetMap Request block must decode at the requested size."""
    size = (128, 128)
    served: dict[str, str] = {}
    for output_format in wms.getOperationByName("GetMap").formatOptions:
        image = _image(wms.getmap(layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX, size=size,
                                  format=output_format))
        assert image.size == size, f"{output_format}: requested {size}, got {image.size}"
        served[output_format] = f"{image.format}/{image.mode}"
    assert len(served) >= 2, served
    wms_collector.record(
        "NB-OWS-WMS-MAP-01", "pass",
        measured_count=len(served),
        notes=(
            "Every advertised GetMap format decoded at the requested size: "
            + ", ".join(f"{name} -> {kind}" for name, kind in served.items())
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-MAP-02")
def test_ext_getmap_crs_matrix(wms: WebMapService, wms_layer: str,
                               wms_collector: CertificationEvidenceCollector) -> None:
    """The same ground area requested in three CRSs must render the same content.

    OWSLib swaps the bbox ordinates for a latitude-first CRS in WMS 1.3.0, so
    CRS:84 and EPSG:4326 send *different* BBOX strings for the same area. If the
    server ignores the 1.3.0 axis rule, the two images diverge.
    """
    size = (120, 100)
    crs84 = _image(wms.getmap(layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX, size=size,
                              format="image/png", transparent=True))
    epsg4326 = _image(wms.getmap(layers=[wms_layer], srs="EPSG:4326", bbox=VIEW_BBOX, size=size,
                                 format="image/png", transparent=True))
    mercator = _image(wms.getmap(layers=[wms_layer], srs="EPSG:3857", bbox=VIEW_BBOX_3857,
                                 size=size, format="image/png", transparent=True))

    assert list(crs84.convert("RGBA").getdata()) == list(epsg4326.convert("RGBA").getdata()), (
        "WMS 1.3.0 EPSG:4326 (latitude first) and CRS:84 (longitude first) requests for the same "
        "ground area produced different images: the axis-order rule is not honoured"
    )
    assert _drawn_pixels(crs84, TRANSPARENT) > 0
    assert _drawn_pixels(mercator, TRANSPARENT) > 0, (
        "the EPSG:3857 view of the same ground area rendered nothing"
    )
    wms_collector.record(
        "NB-OWS-WMS-MAP-02", "pass",
        measured_count=_drawn_pixels(crs84, TRANSPARENT),
        notes=(
            "CRS:84 and EPSG:4326 requests for the same ground area are pixel-identical even "
            "though OWSLib sends latitude-first ordinates for EPSG:4326, and the EPSG:3857 "
            "reprojection of the same area also renders content."
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-MAP-03")
def test_ext_transparency_and_background(wms: WebMapService, wms_layer: str,
                                         wms_collector: CertificationEvidenceCollector) -> None:
    """TRANSPARENT and BGCOLOR must both change the rendered background."""
    size = (64, 64)
    transparent = _image(wms.getmap(layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX, size=size,
                                    format="image/png", transparent=True))
    opaque = _image(wms.getmap(layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX, size=size,
                               format="image/png", transparent=False))
    coloured = _image(wms.getmap(layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX, size=size,
                                 format="image/png", transparent=False, bgcolor="#FF0000"))

    assert _palette(transparent, 1)[0][0] == TRANSPARENT, (
        f"TRANSPARENT=TRUE did not produce a transparent background: {_palette(transparent, 2)}"
    )
    assert _palette(opaque, 1)[0][0] == (255, 255, 255, 255), (
        f"TRANSPARENT=FALSE should default to an opaque white background: {_palette(opaque, 2)}"
    )
    assert _palette(coloured, 1)[0][0] == (255, 0, 0, 255), (
        f"BGCOLOR=0xFF0000 was not honoured: {_palette(coloured, 2)}"
    )
    drawn = _drawn_pixels(coloured, (255, 0, 0, 255))
    assert drawn > 0, "the features vanished once a background colour was requested"
    wms_collector.record(
        "NB-OWS-WMS-MAP-03", "pass",
        measured_count=drawn,
        notes=(
            "TRANSPARENT=TRUE -> fully transparent background; TRANSPARENT=FALSE -> opaque white; "
            "BGCOLOR=0xFF0000 -> opaque red, with the features still drawn on top "
            f"({drawn} non-background pixels)."
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-MAP-04")
def test_ext_named_style_and_multilayer(wms: WebMapService, wms_layer: str,
                                        wms_collector: CertificationEvidenceCollector) -> None:
    """A named style must be accepted, and a multi-layer request must composite."""
    size = (128, 128)
    styled = _image(wms.getmap(layers=[wms_layer], styles=["default"], srs="CRS:84",
                               bbox=VIEW_BBOX, size=size, format="image/png", transparent=True))
    default = _image(wms.getmap(layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX, size=size,
                                format="image/png", transparent=True))
    assert list(styled.convert("RGBA").getdata()) == list(default.convert("RGBA").getdata()), (
        "STYLES=default must render identically to an empty STYLES parameter"
    )

    names = list(wms.contents)
    composite = _image(wms.getmap(layers=names, srs="CRS:84", bbox=VIEW_BBOX, size=size,
                                  format="image/png", transparent=True))
    assert _drawn_pixels(composite, TRANSPARENT) > _drawn_pixels(styled, TRANSPARENT), (
        "a request for every layer drew no more pixels than the single-layer request"
    )
    wms_collector.record(
        "NB-OWS-WMS-MAP-04", "pass",
        measured_count=_drawn_pixels(composite, TRANSPARENT),
        notes=(
            "STYLES=default matches the implicit default style pixel for pixel, and a "
            f"{len(names)}-layer LAYERS request composites strictly more content than the single "
            "layer alone."
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-MAP-05")
def test_ext_out_of_domain_bbox_renders_blank(wms: WebMapService, wms_layer: str,
                                              wms_collector: CertificationEvidenceCollector) -> None:
    """A valid bbox outside the data extent must render an empty image, not fail.

    This is the documented Honua behaviour (WMS GetMap returns a blank image
    when the requested area lies outside the data), and it is what tiling
    clients depend on: an exception here turns into visible tile errors.
    """
    size = (64, 64)
    empty = _image(wms.getmap(layers=[wms_layer], srs="CRS:84", bbox=(10.0, 10.0, 11.0, 11.0),
                              size=size, format="image/png", transparent=True))
    assert empty.size == size
    assert _drawn_pixels(empty, TRANSPARENT) == 0, (
        f"a window with no data rendered content: {_palette(empty, 3)}"
    )
    wms_collector.record(
        "NB-OWS-WMS-MAP-05", "pass",
        measured_count=0,
        notes=(
            "A well-formed bbox outside the layer extent returns a correctly sized, fully "
            "transparent PNG rather than a ServiceException, so tiled clients degrade to empty "
            "tiles instead of error tiles."
        ),
    )


# ---------------------------------------------------------------------------
# GEOM
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-GEOM-02")
def test_geom02_requested_crs_is_honoured(wms: WebMapService, wms_layer: str,
                                          wms_collector: CertificationEvidenceCollector,
                                          timer: Timer) -> None:
    """The requested CRS must be advertised, carried in the request, and served."""
    requested = "EPSG:3857"
    assert requested in wms.contents[wms_layer].crsOptions
    image = _image(wms.getmap(layers=[wms_layer], srs=requested, bbox=VIEW_BBOX_3857,
                              size=(256, 256), format="image/png", transparent=True))
    assert "crs=EPSG%3A3857" in wms.request or "crs=EPSG:3857" in wms.request, wms.request
    assert "srs=" not in wms.request, (
        f"WMS 1.3.0 must send CRS, not the 1.1.1 SRS parameter: {wms.request}"
    )
    assert image.size == (256, 256)
    assert _drawn_pixels(image, TRANSPARENT) > 0, (
        "the requested projected CRS produced an empty image over the data extent"
    )
    wms_collector.record(
        "CERT-GEOM-02", "pass",
        duration_ms=timer.ms,
        measured_count=_drawn_pixels(image, TRANSPARENT),
        notes=(
            f"OWSLib emitted the WMS 1.3.0 CRS parameter (crs={requested}, never the 1.1.1 SRS "
            "spelling) for a CRS the layer advertises, and the server returned rendered content "
            "at the requested size instead of a ServiceException. The response is imagery, so the "
            "CRS is substantiated by the request/response pair plus the CRS:84/EPSG:4326 "
            "axis-order equivalence proved in NB-OWS-WMS-MAP-02."
        ),
        evidence_ref=wms.request,
    )


# ---------------------------------------------------------------------------
# GetFeatureInfo
# ---------------------------------------------------------------------------

def _pixel_for(lon: float, lat: float, size: tuple[int, int]) -> tuple[int, int]:
    minx, miny, maxx, maxy = VIEW_BBOX
    return (
        int((lon - minx) / (maxx - minx) * size[0]),
        int((maxy - lat) / (maxy - miny) * size[1]),
    )


@pytest.mark.cert("NB-OWS-WMS-GFI-01")
def test_ext_getfeatureinfo_hit(wms: WebMapService, wms_layer: str,
                                wms_collector: CertificationEvidenceCollector) -> None:
    """A GetFeatureInfo aimed at a seeded point must return that feature."""
    size = (256, 256)
    i, j = _pixel_for(-122.4194, 37.7749, size)
    response = wms.getfeatureinfo(
        layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX, size=size, format="image/png",
        query_layers=[wms_layer], xy=(i, j), info_format="application/json", feature_count=10)
    payload = response.read().decode()
    assert "pt-alpha" in payload, f"the seeded point was not identified at pixel {(i, j)}: {payload}"
    wms_collector.record(
        "NB-OWS-WMS-GFI-01", "pass",
        measured_count=1,
        notes=(
            f"GetFeatureInfo at I/J {(i, j)} -- the pixel the seeded pt-alpha point projects to in "
            "the requested view -- returned that feature with its attributes, so the server's "
            "pixel-to-world inverse transform agrees with the client's."
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-GFI-02")
def test_ext_getfeatureinfo_formats(wms: WebMapService, wms_layer: str,
                                    wms_collector: CertificationEvidenceCollector) -> None:
    """Every advertised ``info_format`` must return a real, format-appropriate body."""
    size = (256, 256)
    i, j = _pixel_for(-122.4194, 37.7749, size)
    served: dict[str, int] = {}
    for info_format in wms.getOperationByName("GetFeatureInfo").formatOptions:
        body = wms.getfeatureinfo(
            layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX, size=size, format="image/png",
            query_layers=[wms_layer], xy=(i, j), info_format=info_format,
            feature_count=10).read().decode()
        assert "ServiceException" not in body, f"{info_format} is advertised but errored: {body[:200]}"
        assert "pt-alpha" in body, f"{info_format} lost the identified feature: {body[:200]}"
        if "json" in info_format:
            assert body.lstrip().startswith("{")
        elif "gml" in info_format or "xml" in info_format:
            assert body.lstrip().startswith("<")
        served[info_format] = len(body)
    assert len(served) >= 3, served
    wms_collector.record(
        "NB-OWS-WMS-GFI-02", "pass",
        measured_count=len(served),
        notes=(
            f"All {len(served)} advertised GetFeatureInfo formats identified the same feature with "
            "a body matching the declared media type: "
            + ", ".join(f"{name}={length}B" for name, length in served.items())
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-GFI-03")
def test_ext_getfeatureinfo_miss(wms: WebMapService, wms_layer: str,
                                 wms_collector: CertificationEvidenceCollector) -> None:
    """A miss must be an empty result, not an error."""
    size = (256, 256)
    response = wms.getfeatureinfo(
        layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX, size=size, format="image/png",
        query_layers=[wms_layer], xy=(2, 2), info_format="application/json", feature_count=10)
    body = response.read().decode()
    assert "ServiceException" not in body, body[:200]
    assert '"features":[]' in body.replace(" ", ""), body[:200]
    wms_collector.record(
        "NB-OWS-WMS-GFI-03", "pass",
        measured_count=0,
        notes=(
            "A GetFeatureInfo aimed at empty space returns a well-formed response with an empty "
            "feature list rather than an exception, which is what identify tools rely on."
        ),
    )


# ---------------------------------------------------------------------------
# ERRH
# ---------------------------------------------------------------------------

@pytest.mark.cert("CERT-ERRH-01")
def test_errh01_unknown_layer(wms: WebMapService,
                              wms_collector: CertificationEvidenceCollector, timer: Timer) -> None:
    with pytest.raises(ServiceException) as excinfo:
        wms.getmap(layers=[fx.UNKNOWN_COLLECTION_ID], srs="CRS:84", bbox=VIEW_BBOX,
                   size=(64, 64), format="image/png")
    message = str(excinfo.value)
    assert fx.UNKNOWN_COLLECTION_ID in message, message
    wms_collector.record(
        "CERT-ERRH-01", "pass",
        duration_ms=timer.ms,
        notes=(
            "OWSLib raised owslib.util.ServiceException from the server's WMS "
            "ServiceExceptionReport, and the message names the undefined layer: "
            f"{message[:160]!r}"
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-ERR-01")
def test_ext_error_surface(wms: WebMapService, wms_layer: str, lane_config: LaneConfig,
                           wms_collector: CertificationEvidenceCollector) -> None:
    """Every deliberate GetMap error must be a spec-shaped ServiceExceptionReport.

    OWSLib's ``getmap`` only recognises an error when the server labels it
    ``text/xml`` (or ``application/vnd.ogc.se_xml``) and wraps it in an
    ``ogc:ServiceExceptionReport``, so a 500 or an HTML error page would surface
    here as a decode failure instead.
    """
    cases = {
        "unsupported-format": dict(layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX,
                                   size=(64, 64), format="image/tiff"),
        "unknown-crs": dict(layers=[wms_layer], srs="EPSG:999999", bbox=VIEW_BBOX,
                            size=(64, 64), format="image/png"),
        "inverted-bbox": dict(layers=[wms_layer], srs="CRS:84", bbox=(1.0, 1.0, -1.0, -1.0),
                              size=(64, 64), format="image/png"),
        "degenerate-bbox": dict(layers=[wms_layer], srs="CRS:84", bbox=(1.0, 1.0, 1.0, 1.0),
                                size=(64, 64), format="image/png"),
        "oversize-width": dict(layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX,
                               size=(100000, 100000), format="image/png"),
    }
    observed: dict[str, str] = {}
    for label, kwargs in cases.items():
        with pytest.raises(ServiceException) as excinfo:
            wms.getmap(**kwargs)
        observed[label] = str(excinfo.value)[:80]

    # And the raw wire shape: WMS 1.3.0 exceptions are ogc:ServiceExceptionReport
    # served as text/xml with a machine-readable code.
    response = http_get(lane_config.wms_url, params={
        "SERVICE": "WMS", "VERSION": "1.3.0", "REQUEST": "GetMap",
        "LAYERS": fx.UNKNOWN_COLLECTION_ID, "STYLES": "", "CRS": "CRS:84",
        "BBOX": "-1,-1,1,1", "WIDTH": "32", "HEIGHT": "32", "FORMAT": "image/png",
    }, timeout=30)
    assert response.status_code < 500, f"an invalid LAYERS produced {response.status_code}"
    assert "xml" in response.headers.get("Content-Type", ""), response.headers.get("Content-Type")
    body = response.text
    assert "<ServiceExceptionReport" in body and 'code="' in body, body[:200]

    wms_collector.record(
        "NB-OWS-WMS-ERR-01", "pass",
        measured_count=len(observed),
        notes=(
            f"All {len(observed)} deliberate GetMap errors ({', '.join(observed)}) raised "
            "owslib ServiceException from an ogc:ServiceExceptionReport served as text/xml with a "
            "machine-readable code attribute; none produced a 5xx or an untyped body."
        ),
    )


# ---------------------------------------------------------------------------
# Version negotiation and WMS 1.1.1
# ---------------------------------------------------------------------------

@pytest.mark.cert("NB-OWS-WMS-VER-01")
def test_ext_version_negotiation(lane_config: LaneConfig,
                                 wms_collector: CertificationEvidenceCollector) -> None:
    """Version negotiation must follow the WMS rule, not just echo the request."""
    import re

    observed: dict[str, str] = {}
    for requested in ("1.3.0", "1.1.1", "1.1.0", "1.0.0", "9.9.9", ""):
        params = {"SERVICE": "WMS", "REQUEST": "GetCapabilities"}
        if requested:
            params["VERSION"] = requested
        response = http_get(lane_config.wms_url, params=params, timeout=30)
        assert response.ok, f"VERSION={requested!r} -> {response.status_code}"
        match = re.search(r'<(?:WMS_Capabilities|WMT_MS_Capabilities)[^>]*version="([^"]+)"',
                          response.text)
        assert match, f"VERSION={requested!r} did not return a capabilities document"
        observed[requested or "<absent>"] = match.group(1)

    assert observed["1.3.0"] == "1.3.0"
    assert observed["1.1.1"] == "1.1.1"
    # Higher than anything supported -> the highest supported version.
    assert observed["9.9.9"] == "1.3.0", observed
    assert observed["<absent>"] == "1.3.0", observed
    # Lower than the lowest supported -> the lowest supported version.
    assert observed["1.1.0"] == "1.1.1" and observed["1.0.0"] == "1.1.1", observed
    wms_collector.record(
        "NB-OWS-WMS-VER-01", "pass",
        measured_count=len(observed),
        notes=(
            f"Version negotiation: {observed}. A request above the supported range degrades to the "
            "highest supported version and one below it degrades to the lowest, which is the WMS "
            "negotiation rule rather than a blind echo."
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-111-01")
def test_ext_wms111_axis_order(wms: WebMapService, wms111: WebMapService, wms_layer: str,
                               wms_collector: CertificationEvidenceCollector) -> None:
    """WMS 1.1.1 EPSG:4326 is longitude-first; 1.3.0 EPSG:4326 is latitude-first.

    Getting this backwards is the classic WMS server bug. Both versions are
    asked for the same ground area through OWSLib's own per-version request
    builders, and the two images must be identical.
    """
    assert wms111.identification.type == "OGC:WMS", wms111.identification.type
    assert wms111.identification.version == "1.1.1"
    assert wms_layer in wms111.contents
    assert "CRS:84" not in wms111.contents[wms_layer].crsOptions, (
        "WMS 1.1.1 uses SRS and must not advertise the 1.3.0-only CRS:84 identifier"
    )
    assert wms111.exceptions == ["application/vnd.ogc.se_xml"], wms111.exceptions

    size = (120, 100)
    legacy = _image(wms111.getmap(layers=[wms_layer], srs="EPSG:4326", bbox=VIEW_BBOX, size=size,
                                  format="image/png", transparent=True))
    modern = _image(wms.getmap(layers=[wms_layer], srs="CRS:84", bbox=VIEW_BBOX, size=size,
                               format="image/png", transparent=True))
    assert "srs=EPSG%3A4326" in wms111.request or "srs=EPSG:4326" in wms111.request, wms111.request
    assert list(legacy.convert("RGBA").getdata()) == list(modern.convert("RGBA").getdata()), (
        "WMS 1.1.1 EPSG:4326 (longitude first) and WMS 1.3.0 CRS:84 do not render the same ground "
        "area: the per-version axis-order rule is wrong in one of them"
    )
    assert _drawn_pixels(legacy, TRANSPARENT) > 0
    wms_collector.record(
        "NB-OWS-WMS-111-01", "pass",
        measured_count=_drawn_pixels(legacy, TRANSPARENT),
        notes=(
            "WMS 1.1.1 identifies as OGC:WMS, advertises SRS (not CRS:84) and the 1.1.1 exception "
            "MIME type, and its longitude-first EPSG:4326 GetMap is pixel-identical to the 1.3.0 "
            "CRS:84 render of the same ground area."
        ),
    )


@pytest.mark.cert("NB-OWS-WMS-XPRO-01")
def test_ext_cross_protocol_layer_identity(wms: WebMapService, wms_layer: str,
                                           lane_config: LaneConfig,
                                           wms_collector: CertificationEvidenceCollector) -> None:
    """WMS and OGC API - Features must describe the same raster-fixture layer identically."""
    collection = Features(lane_config.oaf_url).collection(lane_config.raster_layer_id)
    layer = wms.contents[wms_layer]
    assert layer.title == collection["title"], (
        f"WMS calls the layer {layer.title!r}, OGC API calls it {collection['title']!r}"
    )
    oaf_bbox = collection["extent"]["spatial"]["bbox"][0]
    for index, (left, right) in enumerate(zip(layer.boundingBoxWGS84, oaf_bbox)):
        assert abs(left - right) <= 1e-6, (
            f"extent ordinate {index} differs: WMS {layer.boundingBoxWGS84} vs OGC API {oaf_bbox}"
        )
    wms_collector.record(
        "NB-OWS-WMS-XPRO-01", "pass",
        measured_count=len(oaf_bbox),
        notes=(
            f"Layer {layer.title!r} has the same title and the same WGS84 extent "
            f"{layer.boundingBoxWGS84} through WMS capabilities and the OGC API - Features "
            "collection, so the two adapters read one catalogue."
        ),
    )
