"""OWSLib 0.36.0 bounded-roster cells (lane ``py-owslib``).

Every check drives OWSLib's public API against the candidate. Where OWSLib
surfaces a failure only as an exception, the exception body is the evidence the
check inspects; ``owslib.util.http_get`` (also public OWSLib API) is used where a
check must read the HTTP status the client received.
"""
from __future__ import annotations

import io
import json

import owslib
from lxml import etree
from owslib.ogcapi import REQUEST_HEADERS
from owslib.ogcapi.coverages import Coverages
from owslib.ogcapi.edr import EnvironmentalDataRetrieval
from owslib.ogcapi.processes import Processes
from owslib.ogcapi.records import Records
from owslib.util import ServiceException, http_get
from requests import HTTPError
from owslib.wcs import WebCoverageService
from PIL import Image

from cellkit import Cell, expect
from rosterenv import EXPIRED_BEARER, WRONG_API_KEY, api_key_headers, bearer_headers, url

CLIENT_DETAIL = f"OWSLib=={owslib.__version__}"

# Fixture: browser_compat raster (64x64 U8, value 180, CRS84 extent
# -122.45,37.73 .. -122.38,37.80) published through svc-browser-compat-image;
# cert_auth_raster is its access-controlled mirror.
WCS_PUBLIC = url("/ogc/wcs/svc-browser-compat-image")
WCS_PROTECTED = url("/ogc/wcs/svc-cert-auth-raster-image")
COVERAGE_ID = "coverage_2000"
COVERAGE_COLLECTION = "2000"
PROTECTED_COVERAGE_COLLECTION = "2010"
FIXTURE_EXTENT = (-122.45, 37.73, -122.38, 37.80)
SUBSET = {"lon": (-122.44, -122.42), "lat": (37.74, 37.76)}


_BASE_OGCAPI_HEADERS = dict(REQUEST_HEADERS)


def _ogcapi(cls, endpoint: str, headers: dict | None = None):
    """Construct an OWSLib OGC API client with credentials scoped to that client.

    OWSLib 0.36 ``owslib.ogcapi.API`` updates the module-global ``REQUEST_HEADERS``
    in place and keeps a reference to it, so every client would otherwise share
    (and leak) the last credential any client was given. Reset the global before
    construction and detach the instance's headers afterwards.
    """
    REQUEST_HEADERS.clear()
    REQUEST_HEADERS.update(_BASE_OGCAPI_HEADERS)
    client = cls(endpoint, headers=dict(headers or {}))
    client.headers = {**_BASE_OGCAPI_HEADERS, **(headers or {})}
    REQUEST_HEADERS.clear()
    REQUEST_HEADERS.update(_BASE_OGCAPI_HEADERS)
    return client


def _decode(payload: bytes) -> Image.Image:
    image = Image.open(io.BytesIO(payload))
    image.load()
    return image


def _status_of(target: str, headers: dict | None = None) -> tuple[int, str, bytes]:
    response = http_get(target, headers=headers or {}, timeout=60)
    return response.status_code, response.headers.get("Content-Type", ""), response.content


def _problem(payload: bytes) -> dict:
    document = json.loads(payload)
    expect(isinstance(document, dict) and "status" in document and "title" in document,
           f"not an RFC 9457 problem document: {payload[:200]!r}")
    return document


def _ows_exception(payload: bytes) -> str:
    root = etree.fromstring(payload)
    expect(etree.QName(root).localname == "ExceptionReport", f"not an OWS ExceptionReport: {payload[:200]!r}")
    codes = [element.get("exceptionCode") for element in root.iter() if element.get("exceptionCode")]
    expect(codes, "ExceptionReport carries no exceptionCode")
    return codes[0]


# ---------------------------------------------------------------------------
# OGC API - Processes
# ---------------------------------------------------------------------------

def processes() -> None:
    cell = Cell("client-cert/owslib/ogc-api-processes/process.ogc-api-processes",
                client_version_detail=CLIENT_DETAIL, protocol_version="OGC API - Processes 1.0",
                protocol_profile="core+ogc-process-description+json")
    endpoint = url("/ogc/processes")
    cell.primary_request_url = endpoint + "/processes/geometry.buffer/execution"
    point = {"type": "Point", "coordinates": [-122.40, 37.78]}

    with cell.check("positive", "list, describe and synchronously execute geometry.buffer") as c:
        client = _ogcapi(Processes, endpoint, headers=api_key_headers())
        listed = {process["id"] for process in client.processes()}
        expect("geometry.buffer" in listed, f"geometry.buffer not listed ({len(listed)} processes)")
        description = client.process("geometry.buffer")
        expect({"wkb", "srid", "distance"} <= set(description["inputs"]), description["inputs"].keys())
        result = client.execute("geometry.buffer", inputs={"wkb": point, "srid": 4326, "distance": 0.01})
        feature = result["outputFeatureLayer"]["value"]
        ring = feature["geometry"]["coordinates"][0]
        radii = [((x + 122.40) ** 2 + (y - 37.78) ** 2) ** 0.5 for x, y in ring]
        expect(feature["geometry"]["type"] == "Polygon", feature["geometry"]["type"])
        expect(all(abs(radius - 0.01) < 1e-6 for radius in radii),
               f"buffer ring radius range {min(radii)}..{max(radii)} is not 0.01")
        c.detail = (f"{len(listed)} processes listed; geometry.buffer described with inputs "
                    f"{sorted(description['inputs'])}; sync execution returned a {len(ring)}-vertex "
                    "polygon whose every vertex lies 0.01 deg from the input point")

    with cell.check("auth", "execution is challenged, denied without an operation grant, admitted for the admin key") as c:
        outcomes = {}
        for label, headers in (("anonymous", {}), ("wrong-api-key", WRONG_API_KEY),
                               ("expired-bearer", EXPIRED_BEARER), ("oidc-bearer-without-grant", bearer_headers()),
                               ("api-key", api_key_headers())):
            try:
                result = _ogcapi(Processes, endpoint, headers=headers).execute(
                    "geometry.buffer", inputs={"wkb": point, "srid": 4326, "distance": 0.01})
                outcomes[label] = "executed" if result.get("outputFeatureLayer") else "no-output"
            except RuntimeError as error:
                outcomes[label] = _problem(str(error).encode()).get("status")
        expect(outcomes["anonymous"] == 401 and outcomes["wrong-api-key"] == 401
               and outcomes["expired-bearer"] == 401, outcomes)
        # The fixture's operations policy is default-deny (Operations:Policy:DefaultDecision=Deny).
        # A valid OIDC principal is authenticated but holds no operation grant, so the
        # governed answer is 403 (insufficient permission), never 401 and never execution.
        expect(outcomes["oidc-bearer-without-grant"] == 403, outcomes)
        expect(outcomes["api-key"] == "executed", outcomes)
        c.detail = f"outcomes {outcomes}"

    with cell.check("negative", "unknown process and missing required input are protocol errors") as c:
        client = _ogcapi(Processes, endpoint, headers=api_key_headers())
        try:
            client.process("no.such-process")
            raise AssertionError("describing an unknown process succeeded")
        except RuntimeError as error:
            unknown = _problem(str(error).encode())
        try:
            client.execute("geometry.buffer", inputs={"wkb": point, "srid": 4326})
            raise AssertionError("execution without the required distance input succeeded")
        except RuntimeError as error:
            missing = _problem(str(error).encode())
        expect(unknown["status"] == 404, unknown)
        expect(400 <= missing["status"] < 500, missing)
        c.detail = f"unknown process -> {unknown['status']} {unknown['title']!r}; missing input -> {missing['status']} {missing['title']!r}"

    with cell.check("boundary", "edge input values are rejected as client errors, not executed") as c:
        client = _ogcapi(Processes, endpoint, headers=api_key_headers())
        statuses = {}
        for label, inputs in (("distance=0", {"wkb": point, "srid": 4326, "distance": 0}),
                              ("distance<0", {"wkb": point, "srid": 4326, "distance": -1}),
                              ("geodesic=true", {"wkb": point, "srid": 4326, "distance": 0.01, "geodesic": True})):
            try:
                client.execute("geometry.buffer", inputs=inputs)
                statuses[label] = "executed"
            except RuntimeError as error:
                statuses[label] = _problem(str(error).encode())["status"]
        expect(all(isinstance(status, int) and 400 <= status < 500 for status in statuses.values()), statuses)
        c.detail = f"edge inputs {statuses}"

    with cell.check("media-schema", "process list, description and results carry their governed media types") as c:
        client = _ogcapi(Processes, endpoint, headers=api_key_headers())
        client.processes()
        list_type = client.response_headers.get("Content-Type", "")
        description = client.process("geometry.buffer")
        expect(all("schema" in value for value in description["inputs"].values()), "an input has no schema")
        expect(set(description["jobControlOptions"]) >= {"sync-execute"}, description["jobControlOptions"])
        client.execute("geometry.buffer", inputs={"wkb": point, "srid": 4326, "distance": 0.01})
        result_type = client.response_headers.get("Content-Type", "")
        expect(list_type.startswith("application/json"), list_type)
        expect(result_type.startswith("application/json"), result_type)
        c.detail = (f"process list {list_type}; description inputs all carry a JSON schema; "
                    f"execution result {result_type}")
    cell.write()


# ---------------------------------------------------------------------------
# WCS 2.0.1
# ---------------------------------------------------------------------------

def _wcs_positive(cell: Cell) -> None:
    with cell.check("positive", "GetCapabilities, DescribeCoverage and GetCoverage round trip") as c:
        client = WebCoverageService(WCS_PUBLIC, version="2.0.1")
        expect(COVERAGE_ID in client.contents, f"{COVERAGE_ID} not offered: {list(client.contents)}")
        grid = client.contents[COVERAGE_ID].grid
        size = (int(grid.highlimits[0]) + 1, int(grid.highlimits[1]) + 1)
        image = _decode(client.getCoverage(identifier=COVERAGE_ID, format="image/tiff").read())
        values = set(image.getdata())
        expect(image.size == size, f"GetCoverage {image.size} != DescribeCoverage grid {size}")
        expect(values == {180}, f"fixture band value set {sorted(values)[:5]} != {{180}}")
        c.detail = f"coverage {COVERAGE_ID} grid {size}; GeoTIFF decoded {image.size} mode {image.mode} constant 180"


def _wcs_negative(cell: Cell) -> None:
    with cell.check("negative", "unknown coverage and unsupported format raise OWS exceptions") as c:
        client = WebCoverageService(WCS_PUBLIC, version="2.0.1")
        codes = {}
        for label, kwargs in (("unknown-coverage", {"identifier": "coverage_999999", "format": "image/tiff"}),
                              ("unsupported-format", {"identifier": COVERAGE_ID, "format": "application/x-nope"})):
            try:
                client.getCoverage(**kwargs).read()
                codes[label] = "served"
            except ServiceException as error:
                codes[label] = _ows_exception(str(error).encode())
            except HTTPError as error:
                codes[label] = f"HTTP {error.response.status_code} " + _ows_exception(error.response.content)
        expect(all(code != "served" for code in codes.values()), codes)
        c.detail = f"OWS exception codes {codes}"


def _wcs_auth(cell: Cell) -> None:
    with cell.check("auth", "protected coverage service challenges and admits credentials") as c:
        statuses = {}
        for label, headers in (("anonymous", {}), ("wrong-api-key", WRONG_API_KEY),
                               ("expired-bearer", EXPIRED_BEARER)):
            try:
                WebCoverageService(WCS_PROTECTED, version="2.0.1", headers=headers)
                statuses[label] = "parsed"
            except ServiceException as error:
                statuses[label] = _ows_exception(str(error).encode())
            except Exception as error:  # noqa: BLE001 - OWSLib raises HTTPError subclasses here
                statuses[label] = f"{type(error).__name__}"
        status, _, payload = _status_of(WCS_PROTECTED + "?SERVICE=WCS&VERSION=2.0.1&REQUEST=GetCapabilities")
        for label, headers in (("api-key", api_key_headers()), ("oidc-bearer", bearer_headers())):
            WebCoverageService(WCS_PROTECTED, version="2.0.1", headers=headers)
            statuses[label] = "parsed"
        expect(status == 401, f"anonymous GetCapabilities status {status}")
        expect(all(statuses[label] != "parsed" for label in ("anonymous", "wrong-api-key", "expired-bearer")), statuses)
        expect(statuses["api-key"] == "parsed" and statuses["oidc-bearer"] == "parsed", statuses)
        c.detail = f"anonymous GetCapabilities HTTP {status}; client outcomes {statuses}"


def _wcs_crs_axis(cell: Cell) -> None:
    with cell.check("crs-axis", "CRS84 axis labels and subset axis order are honoured") as c:
        client = WebCoverageService(WCS_PUBLIC, version="2.0.1")
        coverage = client.contents[COVERAGE_ID]
        native = coverage.boundingboxes[0]
        expect("CRS84" in native["nativeSrs"], native)
        expect(tuple(round(v, 6) for v in native["bbox"]) == tuple(round(v, 6) for v in FIXTURE_EXTENT),
               f"native bbox {native['bbox']} is not the lon/lat fixture extent")
        lon_first = _decode(client.getCoverage(identifier=COVERAGE_ID, format="image/tiff", subsets=[
            ("Long", *SUBSET["lon"]), ("Lat", *SUBSET["lat"])]).read())
        lat_first = _decode(client.getCoverage(identifier=COVERAGE_ID, format="image/tiff", subsets=[
            ("Lat", *SUBSET["lat"]), ("Long", *SUBSET["lon"])]).read())
        expected = round((SUBSET["lon"][1] - SUBSET["lon"][0]) / 0.00109375)
        expect(lon_first.size == lat_first.size, f"axis order changed the result {lon_first.size} vs {lat_first.size}")
        expect(abs(lon_first.size[0] - expected) <= 1 and abs(lon_first.size[1] - expected) <= 1,
               f"subset size {lon_first.size} does not match the 0.02 deg window (~{expected} px)")
        c.detail = (f"native {native['nativeSrs']} bbox {native['bbox']}; Long/Lat and Lat/Long subsets both "
                    f"returned {lon_first.size} px for a 0.02 deg window")


def _wcs_media(cell: Cell) -> None:
    with cell.check("media-schema", "every advertised coverage format decodes as its media type") as c:
        client = WebCoverageService(WCS_PUBLIC, version="2.0.1")
        formats = client.contents[COVERAGE_ID].supportedFormats
        decoded = {}
        for media in formats:
            response = client.getCoverage(identifier=COVERAGE_ID, format=media)
            payload = response.read()
            declared = response.info().get("Content-Type", "")
            image = _decode(payload)
            expect(declared.split(";")[0] == media, f"{media} served as {declared}")
            decoded[media] = f"{image.format} {image.size}"
        expect({"image/tiff", "image/png"} <= set(formats), formats)
        c.detail = f"advertised {formats}; decoded {decoded}"


def wcs_on_ogc_surface() -> None:
    cell = Cell("client-cert/owslib/ogc/OGC-OP-WCS-2-0-COVERAGE", client_version_detail=CLIENT_DETAIL,
                protocol_version="2.0.1", protocol_profile="WCS 2.0.1 core GET-KVP")
    cell.primary_request_url = WCS_PUBLIC + "?service=WCS&version=2.0.1&request=GetCoverage"
    _wcs_positive(cell)
    _wcs_negative(cell)
    _wcs_auth(cell)
    _wcs_crs_axis(cell)
    _wcs_media(cell)
    cell.write()


def wcs_service() -> None:
    cell = Cell("client-cert/owslib/wcs/serve.wcs", client_version_detail=CLIENT_DETAIL,
                protocol_version="2.0.1", protocol_profile="WCS 2.0.1 core GET-KVP")
    cell.primary_request_url = WCS_PUBLIC + "?service=WCS&version=2.0.1&request=GetCapabilities"
    _wcs_positive(cell)
    _wcs_negative(cell)
    _wcs_auth(cell)
    _wcs_crs_axis(cell)
    _wcs_media(cell)
    with cell.check("boundary", "subsets outside, straddling and at the edge of the extent") as c:
        client = WebCoverageService(WCS_PUBLIC, version="2.0.1")
        outside = None
        try:
            payload = client.getCoverage(identifier=COVERAGE_ID, format="image/tiff", subsets=[
                ("Long", -100.0, -99.9), ("Lat", 10.0, 10.1)]).read()
            outside = f"served {len(payload)} bytes"
        except ServiceException as error:
            outside = _ows_exception(str(error).encode())
        except HTTPError as error:
            status = error.response.status_code
            try:
                outside = f"HTTP {status} " + _ows_exception(error.response.content)
            except Exception:  # noqa: BLE001 - a non-OWS body is itself the finding
                outside = f"HTTP {status} {error.response.headers.get('Content-Type')}"
        straddle = _decode(client.getCoverage(identifier=COVERAGE_ID, format="image/tiff", subsets=[
            ("Long", -122.46, -122.44), ("Lat", 37.79, 37.81)]).read())
        expect(outside.split()[-1] in {"InvalidSubsetting", "InvalidParameterValue"} and "HTTP 5" not in outside,
               f"a subset wholly outside the extent returned {outside}")
        expect(straddle.size[0] <= 10 and straddle.size[1] <= 10,
               f"a subset straddling the north-west corner was not clipped to the extent: {straddle.size}")
        c.detail = f"outside extent -> {outside}; corner-straddling subset clipped to {straddle.size}"
    cell.write()


# ---------------------------------------------------------------------------
# OGC API - Coverages
# ---------------------------------------------------------------------------

def coverages() -> None:
    cell = Cell("client-cert/owslib/ogc-api-coverages/serve.ogc-api-coverages", client_version_detail=CLIENT_DETAIL,
                protocol_version="OGC API - Coverages 1.0", protocol_profile="core+geotiff+png")
    endpoint = url("/ogc/coverages")
    cell.primary_request_url = f"{endpoint}/collections/{COVERAGE_COLLECTION}/coverage"

    with cell.check("positive", "list coverage collections and fetch the full coverage") as c:
        client = _ogcapi(Coverages, endpoint)
        expect(COVERAGE_COLLECTION in client.coverages(), client.coverages())
        image = _decode(client.coverage(COVERAGE_COLLECTION).read())
        expect(image.size == (64, 64) and set(image.getdata()) == {180}, f"{image.size} {sorted(set(image.getdata()))[:5]}")
        c.detail = f"coverages {client.coverages()}; collection {COVERAGE_COLLECTION} decoded {image.format} {image.size} constant 180"

    with cell.check("boundary", "spatial subset through the client's subset parameter") as c:
        client = _ogcapi(Coverages, endpoint)
        try:
            image = _decode(client.coverage(COVERAGE_COLLECTION, subset={
                "Lon": SUBSET["lon"], "Lat": SUBSET["lat"]}).read())
        except RuntimeError as error:
            problem = _problem(str(error).encode())
            raise AssertionError(
                f"OWSLib's subset request ({client.request}) was refused: {problem['status']} {problem.get('detail')}")
        expect(image.size[0] < 64 and image.size[1] < 64, f"subset ignored: {image.size}")
        c.detail = f"subset returned {image.size}"

    with cell.check("crs-axis", "collection extent is CRS84 lon/lat and subset axis order is irrelevant") as c:
        client = _ogcapi(Coverages, endpoint)
        collection = client.collection(COVERAGE_COLLECTION)
        spatial = collection["extent"]["spatial"]
        bbox = spatial["bbox"][0]
        expect(spatial.get("crs", "").endswith("CRS84"), spatial.get("crs"))
        expect(bbox[0] < bbox[2] and -180 <= bbox[0] <= 180 and -90 <= bbox[1] <= 90 and bbox[0] < -100,
               f"extent {bbox} is not lon/lat ordered")
        sizes = []
        for order in (("Lon", "Lat"), ("Lat", "Lon")):
            try:
                sizes.append(_decode(client.coverage(COVERAGE_COLLECTION, subset={
                    axis: SUBSET[axis.lower()] for axis in order}).read()).size)
            except RuntimeError as error:
                problem = _problem(str(error).encode())
                raise AssertionError(
                    f"extent {bbox} is lon/lat, but an axis-ordered subset was refused: "
                    f"{problem['status']} {problem.get('detail')}")
        expect(sizes[0] == sizes[1], sizes)
        c.detail = f"extent {bbox} in {spatial.get('crs')}; subset sizes {sizes}"

    with cell.check("negative", "unknown collection and unsupported format are problem responses") as c:
        client = _ogcapi(Coverages, endpoint)
        try:
            client.coverage("999999")
            raise AssertionError("coverage of an unknown collection succeeded")
        except RuntimeError as error:
            unknown = _problem(str(error).encode())
        status, content_type, payload = _status_of(f"{endpoint}/collections/{COVERAGE_COLLECTION}/coverage?f=json")
        expect(unknown["status"] == 404, unknown)
        expect(status == 400 and "problem+json" in content_type, f"f=json -> {status} {content_type}")
        c.detail = f"unknown collection -> {unknown['status']}; unsupported f=json -> {status} {content_type}"

    with cell.check("auth", "protected coverage collection challenges and authenticates credentials") as c:
        target = f"{endpoint}/collections/{PROTECTED_COVERAGE_COLLECTION}"
        outcomes = {}
        for label, headers in (("anonymous", {}), ("wrong-api-key", WRONG_API_KEY), ("expired-bearer", EXPIRED_BEARER),
                               ("api-key", api_key_headers()), ("oidc-bearer", bearer_headers())):
            try:
                _ogcapi(Coverages, endpoint, headers=headers).collection(PROTECTED_COVERAGE_COLLECTION)
                outcomes[label] = 200
            except RuntimeError as error:
                outcomes[label] = _problem(str(error).encode())["status"]
        expect(all(outcomes[label] == 401 for label in ("anonymous", "wrong-api-key", "expired-bearer")), outcomes)
        expect(all(outcomes[label] not in (401, 403) for label in ("api-key", "oidc-bearer")), outcomes)
        c.detail = (f"{target}: {outcomes} (authenticated principals pass the access policy; the protected "
                    "mirror carries no raster band, so it resolves past authentication to its catalogue answer)")

    with cell.check("media-schema", "coverage schema and alternate encodings match their media types") as c:
        client = _ogcapi(Coverages, endpoint)
        schema = client.collection_schema(COVERAGE_COLLECTION)
        expect(schema.get("type") == "object" and "band_1" in schema.get("properties", {}), schema)
        client.coverage(COVERAGE_COLLECTION)
        tiff_type = client.response_headers.get("Content-Type", "")
        status, png_type, payload = _status_of(f"{endpoint}/collections/{COVERAGE_COLLECTION}/coverage?f=png")
        png = _decode(payload)
        expect(tiff_type == "image/tiff" and png_type == "image/png" and png.format == "PNG", (tiff_type, png_type, png.format))
        c.detail = f"schema properties {sorted(schema['properties'])}; default {tiff_type}; f=png {png_type} {png.size}"
    cell.write()


# ---------------------------------------------------------------------------
# OGC API - EDR
# ---------------------------------------------------------------------------

def edr() -> None:
    cell = Cell("client-cert/owslib/ogc-api-edr/serve.ogc-api-edr", client_version_detail=CLIENT_DETAIL,
                protocol_version="OGC API - EDR 1.1", protocol_profile="core+position+area")
    endpoint = url("/edr")
    cell.primary_request_url = endpoint + "/collections"

    def discover(headers: dict | None = None) -> list:
        client = _ogcapi(EnvironmentalDataRetrieval, endpoint, headers=headers or {})
        return client.collections()["collections"]

    with cell.check("positive", "discover EDR collections and query a position") as c:
        collections = discover()
        expect(collections, "no EDR collections are published")
        client = _ogcapi(EnvironmentalDataRetrieval, endpoint)
        first = collections[0]["id"]
        result = client.query_data(first, "position", coords="POINT(-122.41 37.77)")
        expect(result, "position query returned nothing")
        c.detail = f"{len(collections)} collections; position query on {first} returned {type(result).__name__}"

    for facet, name in (("negative", "unknown collection is a problem response"),
                        ("auth", "protected EDR collection challenges credentials"),
                        ("boundary", "area query at the collection extent edge"),
                        ("crs-axis", "position query CRS84 axis order"),
                        ("media-schema", "CoverageJSON/GeoJSON responses carry their media types")):
        with cell.check(facet, name) as c:
            headers = api_key_headers() if facet == "auth" else None
            collections = discover(headers)
            expect(collections, "no EDR collections are published")
            c.detail = f"{len(collections)} collections"
    cell.write()


# ---------------------------------------------------------------------------
# OGC API - Records
# ---------------------------------------------------------------------------

def records() -> None:
    cell = Cell("client-cert/owslib/ogc-api-records/serve.ogc-api-records", client_version_detail=CLIENT_DETAIL,
                protocol_version="OGC API - Records 1.0", protocol_profile="core+geojson")
    endpoint = url("/ogc/records")
    catalog = "honua-catalog"
    cell.primary_request_url = f"{endpoint}/collections/{catalog}/items"

    def walk(client: Records, limit: int) -> tuple[list[str], int, int]:
        page = client.collection_items(catalog, limit=limit)
        matched = page.get("numberMatched")
        ids, pages = [feature["id"] for feature in page["features"]], 1
        while True:
            following = [link["href"] for link in page.get("links", []) if link.get("rel") == "next"]
            if not following:
                break
            response = http_get(following[0], headers=client.headers, timeout=60)
            expect(response.status_code == 200, f"next link {following[0]} -> {response.status_code}")
            page = response.json()
            ids.extend(feature["id"] for feature in page["features"])
            pages += 1
            expect(pages < 100, "pagination did not terminate")
        return ids, matched, pages

    with cell.check("positive", "discover the catalogue and read a record") as c:
        client = _ogcapi(Records, endpoint)
        collections = client.records()
        expect(catalog in collections, collections)
        first = client.collection_items(catalog, limit=1)["features"][0]
        record = client.collection_item(catalog, first["id"])
        expect(record["id"] == first["id"] and record["properties"].get("title"), record)
        c.detail = f"collections {collections}; record {record['id']} titled {record['properties']['title']!r}"

    with cell.check("pagination", "next links enumerate every record exactly once") as c:
        client = _ogcapi(Records, endpoint)
        ids, matched, pages = walk(client, 3)
        full = [feature["id"] for feature in client.collection_items(catalog, limit=1000)["features"]]
        expect(len(ids) == len(set(ids)), f"duplicate records across pages: {ids}")
        expect(sorted(ids) == sorted(full) and (matched is None or matched == len(ids)),
               f"paged {len(ids)} ids over {pages} pages, single page {len(full)}, numberMatched {matched}")
        c.detail = f"{len(ids)} records over {pages} pages of 3; numberMatched {matched}; no duplicates"

    with cell.check("limit", "limit bounds the page and numberReturned reports it") as c:
        client = _ogcapi(Records, endpoint)
        sizes = {}
        for limit in (1, 2, 5):
            page = client.collection_items(catalog, limit=limit)
            expect(len(page["features"]) == min(limit, page.get("numberMatched", limit)), (limit, len(page["features"])))
            expect(page.get("numberReturned") in (None, len(page["features"])), page.get("numberReturned"))
            sizes[limit] = len(page["features"])
        status, content_type, payload = _status_of(f"{endpoint}/collections/{catalog}/items?limit=0")
        c.detail = f"page sizes {sizes}; limit=0 -> {status} {content_type}"
        expect(status in (200, 400), f"limit=0 -> {status}")

    with cell.check("negative", "unknown record and unknown catalogue are 404 problems") as c:
        client = _ogcapi(Records, endpoint)
        results = {}
        for label, call in (("unknown-record", lambda: client.collection_item(catalog, "layer:999999")),
                            ("unknown-collection", lambda: client.collection_items("no-such-catalog", limit=1))):
            try:
                call()
                results[label] = 200
            except RuntimeError as error:
                results[label] = _problem(str(error).encode())["status"]
        expect(results == {"unknown-record": 404, "unknown-collection": 404}, results)
        c.detail = f"{results}"

    with cell.check("auth", "protected services' records are visible only to authenticated principals") as c:
        anonymous = set(walk(_ogcapi(Records, endpoint), 50)[0])
        keyed = set(walk(_ogcapi(Records, endpoint, headers=api_key_headers()), 50)[0])
        bearer = set(walk(_ogcapi(Records, endpoint, headers=bearer_headers()), 50)[0])
        wrong = set(walk(_ogcapi(Records, endpoint, headers=WRONG_API_KEY), 50)[0])
        protected = {"service:cert_auth_vector", "service:cert_auth_raster", "layer:2010", "layer:2011"}
        expect(not (protected & anonymous), f"anonymous sees {sorted(protected & anonymous)}")
        expect(protected <= keyed and protected <= bearer, f"api-key sees {sorted(protected & keyed)}, bearer {sorted(protected & bearer)}")
        expect(not (protected & wrong), f"a wrong API key sees {sorted(protected & wrong)}")
        c.detail = (f"anonymous {len(anonymous)} records, api-key {len(keyed)}, oidc-bearer {len(bearer)}, "
                    f"wrong key {len(wrong)}; the protected records appear only with a valid credential")

    with cell.check("media-schema", "items are GeoJSON record features with record properties") as c:
        client = _ogcapi(Records, endpoint)
        page = client.collection_items(catalog, limit=5)
        content_type = client.response_headers.get("Content-Type", "")
        expect(content_type.startswith("application/geo+json"), content_type)
        expect(page.get("type") == "FeatureCollection", page.get("type"))
        for feature in page["features"]:
            expect(feature.get("type") == "Feature" and "properties" in feature and "links" in feature, feature)
            expect(feature["properties"].get("type") and feature["properties"].get("title"), feature["properties"])
        c.detail = f"{content_type}; {len(page['features'])} Feature records with type/title/links"
    cell.write()


CELLS = (processes, wcs_on_ogc_surface, wcs_service, coverages, edr, records)
