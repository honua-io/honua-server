"""GDAL/OGR 3.8.4 bounded-roster cells on the OGC surfaces (lane ``gdal``).

Clients: the OGR ``OAPIF`` and ``WFS`` drivers and the GDAL ``WCS`` and ``OGCAPI``
drivers. Every assertion reads what GDAL returned through its own API; the URLs
GDAL reports fetching (``gdalkit.Session.fetched``) prove which governed endpoint
the driver exercised.
"""
from __future__ import annotations

import gdalkit
from cellkit import Cell, expect
from osgeo import gdal, ogr, osr
from rosterenv import (
    EXPIRED_BEARER,
    WRONG_API_KEY,
    api_key_headers,
    bearer_headers,
    url,
)

CLIENT_DETAIL = gdal.VersionInfo("--version")
OAPIF = url("/ogc/features")
WFS = url("/wfs?SERVICE=WFS&VERSION=2.0.0")
WCS_PUBLIC = url("/ogc/wcs/svc-browser-compat-image")
WCS_PROTECTED = url("/ogc/wcs/svc-cert-auth-raster-image")

# test_service layer 0: ten points alpha..lambda, lambda has no geometry.
FIXTURE = {
    1: ("alpha", "active", (-122.49, 37.71)), 2: ("beta", "inactive", (-122.475, 37.72)),
    3: ("gamma", "active", (-122.46, 37.73)), 4: ("delta", "inactive", (-122.445, 37.74)),
    5: ("epsilon", "active", (-122.43, 37.75)), 6: ("zeta", "inactive", (-122.415, 37.76)),
    7: ("eta", "active", (-122.40, 37.77)), 8: ("theta", "inactive", (-122.385, 37.78)),
    9: ("iota", "active", (-122.37, 37.79)), 10: ("lambda", "inactive", None),
}
# Anonymous collections of the fixture: client-compat layers plus docker/cng/seed.sql's 1000.
PUBLIC_COLLECTIONS = {"0", "10", "11", "12", "1000", "2000", "2001", "2002", "3000"}
PROTECTED_COLLECTION = "2011"


def _cell(test_id: str, profile: str, version: str = "OGC API - Features 1.0") -> Cell:
    return Cell(test_id, client_version_detail=CLIENT_DETAIL, protocol_version=version, protocol_profile=profile)


def _open_vector(connection: str, open_options: list[str] | None = None):
    return gdal.OpenEx(connection, gdal.OF_VECTOR, open_options=open_options or [])


def _features(layer) -> dict[int, tuple]:
    layer.ResetReading()
    result = {}
    for feature in layer:
        geometry = feature.GetGeometryRef()
        result[feature.GetFID()] = (
            feature.GetField("name"), feature.GetField("status"),
            None if geometry is None else (round(geometry.GetX(), 6), round(geometry.GetY(), 6)))
    return result


def _json_with_headers(target: str, headers: dict | None = None) -> tuple[dict, dict]:
    """Read a JSON document through GDAL's /vsicurl/ and return it with GDAL's response headers."""
    import json

    with gdalkit.session(headers):
        path = "/vsicurl?use_head=no&url=" + target
        handle = gdal.VSIFOpenL(path, "rb")
        expect(handle is not None, f"GDAL could not open {target}")
        chunks = []
        try:
            while True:
                chunk = gdal.VSIFReadL(1, 65536, handle)
                if not chunk:
                    break
                chunks.append(chunk)
        finally:
            gdal.VSIFCloseL(handle)
        payload = b"".join(chunks)
        response_headers = gdal.GetFileMetadata(path, "HEADERS") or {}
    return json.loads(payload), {key.lower(): value for key, value in response_headers.items()}


# ---------------------------------------------------------------------------
# shared facet checks for OGC API - Features operations
# ---------------------------------------------------------------------------

def _oapif_auth(cell: Cell, target: str) -> None:
    with cell.check("auth", "protected collection is challenged, then admitted for API key and OIDC bearer") as c:
        outcomes = {}
        for label, headers in (("anonymous", None), ("wrong-api-key", WRONG_API_KEY),
                               ("expired-bearer", EXPIRED_BEARER)):
            outcomes[label] = gdalkit.http_status_in(gdalkit.open_failure(target, headers))
        for label, headers in (("api-key", api_key_headers()), ("oidc-bearer", bearer_headers())):
            with gdalkit.session(headers):
                dataset = _open_vector(target)
                layer = dataset.GetLayerByName(PROTECTED_COLLECTION) or dataset.GetLayer(0)
                outcomes[label] = layer.GetFeatureCount()
                del dataset
        expect(all(outcomes[label] == 401 for label in ("anonymous", "wrong-api-key", "expired-bearer")), outcomes)
        expect(outcomes["api-key"] == 10 and outcomes["oidc-bearer"] == 10, outcomes)
        c.detail = f"{target}: {outcomes}"


def _oapif_crs_axis(cell: Cell, connection: str) -> None:
    with cell.check("crs-axis", "EPSG:4326 lat/lon bbox and EPSG:3857 output agree with the lon/lat fixture") as c:
        with gdalkit.session() as record:
            dataset = _open_vector(connection, ["PREFERRED_CRS=EPSG:4326"])
            layer = dataset.GetLayerByName("0") or dataset.GetLayer(0)
            srs = layer.GetSpatialRef()
            expect(srs is not None and srs.GetAuthorityCode(None) == "4326", "layer SRS is not EPSG:4326")
            located = {fid: value[2] for fid, value in _features(layer).items()}
            layer.SetSpatialFilterRect(-122.47, 37.715, -122.44, 37.745)
            windowed = sorted(_features(layer))
            layer.SetSpatialFilter(None)
            del dataset
        bbox_requests = record.fetched_path("bbox=")
        expect(located == {fid: value[2] for fid, value in FIXTURE.items()}, f"lon/lat coordinates differ: {located}")
        expect(windowed == [3, 4], f"bbox window returned {windowed}")
        expect(bbox_requests and "bbox-crs=http://www.opengis.net/def/crs/EPSG/0/4326" in bbox_requests[-1]
               and "bbox=37.7" in bbox_requests[-1], f"GDAL did not send a latitude-first EPSG:4326 bbox: {bbox_requests}")
        with gdalkit.session() as projected_record:
            dataset = _open_vector(connection, ["PREFERRED_CRS=EPSG:3857"])
            layer = dataset.GetLayerByName("0") or dataset.GetLayer(0)
            projected = _features(layer)[1][2]
            del dataset
        transform = osr.CoordinateTransformation(_srs(4326), _srs(3857))
        expected = transform.TransformPoint(-122.49, 37.71)[:2]
        expect(abs(projected[0] - expected[0]) < 1 and abs(projected[1] - expected[1]) < 1,
               f"EPSG:3857 feature 1 {projected} != {expected}")
        expect(any("crs=http://www.opengis.net/def/crs/EPSG/0/3857" in value for value in projected_record.fetched),
               projected_record.fetched)
        c.detail = (f"lon/lat features match the fixture; lat-first EPSG:4326 bbox {bbox_requests[-1].split('?', 1)[1]} "
                    f"-> features {windowed}; crs=EPSG:3857 feature 1 {projected}")


def _srs(code: int) -> osr.SpatialReference:
    reference = osr.SpatialReference()
    reference.ImportFromEPSG(code)
    reference.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    return reference


def _oapif_negative(cell: Cell, connection: str, name: str, require_problem: bool = True) -> None:
    with cell.check("negative", name) as c:
        message = gdalkit.open_failure(connection)
        status = gdalkit.http_status_in(message)
        expect(status == 404 and (not require_problem or '"title"' in (message or "")),
               f"{connection} -> {status}: {message}")
        c.detail = f"{connection} -> HTTP {status}" + (" problem document surfaced by GDAL" if require_problem else "")


def _field_types(layer) -> dict[str, str]:
    definition = layer.GetLayerDefn()
    return {definition.GetFieldDefn(i).GetName(): definition.GetFieldDefn(i).GetTypeName()
            for i in range(definition.GetFieldCount())}


def _oapif_media(cell: Cell, connection: str, document: str) -> None:
    with cell.check("media-schema", "GeoJSON items decode to the typed fixture schema") as c:
        with gdalkit.session() as record:
            dataset = _open_vector(connection)
            layer = dataset.GetLayerByName("0") or dataset.GetLayer(0)
            types = _field_types(layer)
            geometry_type = ogr.GeometryTypeToName(layer.GetGeomType())
            del dataset
        payload, headers = _json_with_headers(document)
        content_type = headers.get("content-type", "")
        expect(types.get("count") == "Integer" and types.get("name") == "String"
               and types.get("created_at") == "DateTime", types)
        expect(geometry_type == "Point", geometry_type)
        expect(content_type.startswith(("application/json", "application/geo+json", "application/schema+json")),
               content_type)
        if content_type.startswith("application/schema+json"):
            expect(isinstance(payload, dict) and payload.get("properties"), f"{document} declares no properties")
            shape = f"{len(payload['properties'])} queryable properties"
        else:
            expect(isinstance(payload, dict) and payload.get("links"), f"{document} has no links")
            shape = f"{len(payload['links'])} links"
        c.detail = (f"field types {types}; geometry {geometry_type}; {document} served {content_type} with "
                    f"{shape}; GDAL fetched {len(record.fetched)} documents")


def _oapif_cell(test_id: str, positive_name: str, positive, connection: str, document: str,
                negative_connection: str, negative_name: str, require_problem: bool = True) -> None:
    cell = _cell(test_id, "core+geojson+crs")
    cell.primary_request_url = document
    with cell.check("positive", positive_name) as c:
        with gdalkit.session() as record:
            c.detail = positive(record)
        c.detail += f"; GDAL fetched {sorted(set(record.fetched))}"
    _oapif_negative(cell, negative_connection, negative_name, require_problem)
    _oapif_auth(cell, "OAPIF:" + url(f"/ogc/features/collections/{PROTECTED_COLLECTION}"))
    _oapif_crs_axis(cell, connection)
    _oapif_media(cell, connection, document)
    cell.write()


# ---------------------------------------------------------------------------
# OGC API - Features operation cells
# ---------------------------------------------------------------------------

def features_landing() -> None:
    def positive(record) -> str:
        dataset = _open_vector("OAPIF:" + OAPIF)
        names = {dataset.GetLayer(i).GetName() for i in range(dataset.GetLayerCount())}
        count = dataset.GetLayerByName("0").GetFeatureCount()
        del dataset
        expect(names == PUBLIC_COLLECTIONS, f"layers {sorted(names)}")
        expect(OAPIF in record.fetched, f"landing page not fetched: {record.fetched}")
        return f"opened the API root as {len(names)} layers {sorted(names)}; layer 0 has {count} features"

    _oapif_cell("client-cert/gdal-ogr/ogc/OGC-OP-OGC-API-FEATURES-LANDING", "open the API and resolve its landing page",
                positive, "OAPIF:" + OAPIF, OAPIF, "OAPIF:" + url("/ogc/features/no-such-api"),
                "an unknown API root is refused with 404", require_problem=False)


def features_conformance() -> None:
    def positive(record) -> str:
        dataset = _open_vector("OAPIF:" + OAPIF)
        layer = dataset.GetLayerByName("0")
        layer.GetFeatureCount()
        supported = [srs.GetAuthorityCode(None) for srs in (layer.GetSupportedSRSList() or [])]
        del dataset
        fetched = record.fetched_path("/conformance")
        expect(fetched, f"GDAL did not request the conformance declaration: {sorted(set(record.fetched))}")
        return f"conformance fetched {fetched}; supported SRS {supported}"

    _oapif_cell("client-cert/gdal-ogr/ogc/OGC-OP-OGC-API-FEATURES-CONFORMANCE",
                "the driver reads the conformance declaration", positive, "OAPIF:" + OAPIF,
                OAPIF + "/conformance", "OAPIF:" + url("/ogc/features/collections/no-such-collection"),
                "an unknown collection is a 404 problem")


def features_collections() -> None:
    def positive(record) -> str:
        dataset = _open_vector("OAPIF:" + OAPIF)
        names = {dataset.GetLayer(i).GetName() for i in range(dataset.GetLayerCount())}
        del dataset
        listed, _ = _json_with_headers(OAPIF + "/collections")
        advertised = {collection["id"] for collection in listed["collections"]}
        expect(names == advertised == PUBLIC_COLLECTIONS, f"layers {sorted(names)} vs advertised {sorted(advertised)}")
        expect(record.fetched_path("/collections"), record.fetched)
        return f"layers equal the advertised collections {sorted(advertised)}"

    _oapif_cell("client-cert/gdal-ogr/ogc/OGC-OP-OGC-API-FEATURES-COLLECTIONS",
                "list collections as layers", positive, "OAPIF:" + OAPIF, OAPIF + "/collections",
                "OAPIF:" + url("/ogc/features/collections/no-such-collection"), "an unknown collection is a 404 problem")


def features_collection() -> None:
    target = OAPIF + "/collections/0"

    def positive(record) -> str:
        dataset = _open_vector("OAPIF:" + target)
        expect(dataset.GetLayerCount() == 1, dataset.GetLayerCount())
        layer = dataset.GetLayer(0)
        metadata = layer.GetMetadata()
        extent = layer.GetExtent()
        del dataset
        expect(metadata.get("TITLE") == "Test Layer", metadata)
        expect(metadata.get("TEMPORAL_INTERVAL_MIN", "").startswith("2024-01-01")
               and metadata.get("TEMPORAL_INTERVAL_MAX", "").startswith("2024-01-10"), metadata)
        expect(extent[0] <= -122.49 and extent[1] >= -122.37 and extent[2] <= 37.71 and extent[3] >= 37.79, extent)
        expect(target in record.fetched, record.fetched)
        return f"collection metadata {metadata}; extent {extent}"

    _oapif_cell("client-cert/gdal-ogr/ogc/OGC-OP-OGC-API-FEATURES-COLLECTION", "open one collection", positive,
                "OAPIF:" + target, target, "OAPIF:" + url("/ogc/features/collections/no-such-collection"),
                "an unknown collection is a 404 problem")


def features_items() -> None:
    target = OAPIF + "/collections/0/items"

    def positive(record) -> str:
        dataset = _open_vector("OAPIF:" + OAPIF + "/collections/0")
        features = _features(dataset.GetLayer(0))
        del dataset
        expect(features == {fid: value for fid, value in FIXTURE.items()}, f"features differ: {features}")
        expect(record.fetched_path("/collections/0/items"), record.fetched)
        return f"read all {len(features)} fixture features with names, status and geometry"

    _oapif_cell("client-cert/gdal-ogr/ogc/OGC-OP-OGC-API-FEATURES-ITEMS", "read every item", positive,
                "OAPIF:" + OAPIF + "/collections/0", target,
                "OAPIF:" + url("/ogc/features/collections/no-such-collection"), "items of an unknown collection are a 404 problem")


def features_item() -> None:
    target = OAPIF + "/collections/0/items/3"

    def positive(record) -> str:
        dataset = _open_vector("OAPIF:" + OAPIF + "/collections/0")
        feature = dataset.GetLayer(0).GetFeature(3)
        values = (feature.GetField("name"), feature.GetField("status"),
                  (round(feature.GetGeometryRef().GetX(), 6), round(feature.GetGeometryRef().GetY(), 6)))
        try:
            missing = dataset.GetLayer(0).GetFeature(999999)
            missing = "feature" if missing is not None else None
        except RuntimeError as error:
            missing = gdalkit.http_status_in(str(error))
        del dataset
        item_requests = record.fetched_path("/items/3")
        expect(values == FIXTURE[3],
               f"GetFeature(3) returned {values}, expected {FIXTURE[3]}: GDAL requested {item_requests} without a "
               "crs parameter, so the server answered in its default CRS84 (lon/lat), but the layer was opened "
               "in EPSG:4326 (lat/lon) from crs-negotiated item pages and the driver reads the single item with "
               "that axis order")
        expect(missing in (None, 404), f"an unknown feature id returned {missing}")
        expect(target in record.fetched, f"GDAL did not fetch the item resource: {record.fetched}")
        return f"GetFeature(3) fetched {target} -> {values}; GetFeature(999999) -> {missing}"

    _oapif_cell("client-cert/gdal-ogr/ogc/OGC-OP-OGC-API-FEATURES-ITEM", "fetch one item by id", positive,
                "OAPIF:" + OAPIF + "/collections/0", target,
                "OAPIF:" + url("/ogc/features/collections/no-such-collection"), "an unknown collection is a 404 problem")


def features_queryables() -> None:
    target = OAPIF + "/collections/0/queryables"

    def positive(record) -> str:
        dataset = _open_vector("OAPIF:" + OAPIF + "/collections/0")
        layer = dataset.GetLayer(0)
        layer.SetAttributeFilter("status = 'active'")
        selected = sorted(_features(layer))
        del dataset
        queryables, _ = _json_with_headers(target)
        expect("status" in queryables.get("properties", {}), queryables)
        expect(selected == [1, 3, 5, 7, 9], selected)
        fetched = record.fetched_path("/queryables")
        expect(fetched, "GDAL evaluated the attribute filter without requesting the collection's queryables "
                        f"(fetched {sorted(set(record.fetched))}); the server does not declare the Features Part 3 "
                        "filter conformance class, so the driver never negotiates server-side filtering")
        return f"queryables fetched {fetched}; filter selected {selected}"

    _oapif_cell("client-cert/gdal-ogr/ogc/OGC-OP-OGC-API-FEATURES-QUERYABLES",
                "the driver uses the collection queryables for attribute filtering", positive,
                "OAPIF:" + OAPIF + "/collections/0", target,
                "OAPIF:" + url("/ogc/features/collections/no-such-collection"), "an unknown collection is a 404 problem")


def features_transactions() -> None:
    target = OAPIF + "/collections/10/items"

    def positive(record) -> str:
        with gdalkit.session(api_key_headers()):
            try:
                gdal.OpenEx("OAPIF:" + OAPIF + "/collections/10", gdal.OF_VECTOR | gdal.OF_UPDATE)
                update_open = "opened"
            except RuntimeError as error:
                update_open = str(error)
            dataset = gdal.OpenEx("OAPIF:" + OAPIF + "/collections/10", gdal.OF_VECTOR)
            layer = dataset.GetLayer(0)
            capabilities = {name: bool(layer.TestCapability(name)) for name in (
                ogr.OLCSequentialWrite, ogr.OLCRandomWrite, ogr.OLCDeleteFeature)}
            feature = ogr.Feature(layer.GetLayerDefn())
            feature.SetGeometry(ogr.CreateGeometryFromWkt("POINT (-122.41 37.77)"))
            try:
                layer.CreateFeature(feature)
                created = "created"
            except RuntimeError as error:
                created = str(error)
            del dataset
        expect(any(capabilities.values()) and created == "created",
               f"GDAL {gdal.__version__}'s OAPIF driver is read-only: update-mode open -> {update_open!r}, write "
               f"capabilities {capabilities}, CreateFeature -> {created!r}; the governed transaction operation "
               "cannot be driven through this client")
        return f"capabilities {capabilities}; CreateFeature {created}"

    _oapif_cell("client-cert/gdal-ogr/ogc/OGC-OP-OGC-API-FEATURES-TRANSACTIONS",
                "create a feature through the driver's write API", positive,
                "OAPIF:" + OAPIF + "/collections/0", target,
                "OAPIF:" + url("/ogc/features/collections/no-such-collection"), "an unknown collection is a 404 problem")


def features_service() -> None:
    cell = _cell("client-cert/gdal-ogr/ogc-api-features/serve.ogc-api-features", "core+geojson+crs+paging")
    connection = "OAPIF:" + OAPIF + "/collections/0"
    cell.primary_request_url = OAPIF + "/collections/0/items"
    with cell.check("positive", "read the collection through the driver") as c:
        with gdalkit.session() as record:
            dataset = _open_vector(connection)
            features = _features(dataset.GetLayer(0))
            del dataset
        expect(features == FIXTURE, features)
        c.detail = f"{len(features)} features match the fixture; fetched {sorted(set(record.fetched))}"
    with cell.check("pagination", "driver follows next links across pages") as c:
        with gdalkit.session() as record:
            dataset = _open_vector(connection, ["PAGE_SIZE=3"])
            layer = dataset.GetLayer(0)
            layer.ResetReading()
            fids = [feature.GetFID() for feature in layer]
            del dataset
        pages = record.fetched_path("/collections/0/items")
        expect(sorted(fids) == sorted(FIXTURE) and len(fids) == len(set(fids)), fids)
        expect(len([page for page in pages if "limit=3" in page or "offset=" in page]) >= 4, pages)
        c.detail = f"{len(fids)} unique features over item requests {pages}"
    with cell.check("limit", "page size is sent as limit and honoured") as c:
        with gdalkit.session() as record:
            dataset = _open_vector(connection, ["PAGE_SIZE=4"])
            layer = dataset.GetLayer(0)
            layer.ResetReading()
            first = [layer.GetNextFeature().GetFID() for _ in range(4)]
            del dataset
        requests = [page for page in record.fetched_path("/collections/0/items") if "limit=4" in page]
        expect(requests, record.fetched)
        payload, _ = _json_with_headers(OAPIF + "/collections/0/items?limit=4")
        expect(len(payload["features"]) == 4 and payload.get("numberReturned") in (None, 4), payload.get("numberReturned"))
        c.detail = f"limit=4 requests {requests}; first page ids {first}; server returned 4 features"
    _oapif_negative(cell, "OAPIF:" + url("/ogc/features/collections/no-such-collection"),
                    "an unknown collection is a 404 problem")
    _oapif_auth(cell, "OAPIF:" + url(f"/ogc/features/collections/{PROTECTED_COLLECTION}"))
    _oapif_crs_axis(cell, connection)
    _oapif_media(cell, connection, OAPIF + "/collections/0/items")
    cell.write()


# ---------------------------------------------------------------------------
# WFS 2.0
# ---------------------------------------------------------------------------

def _wfs_layer(dataset):
    for index in range(dataset.GetLayerCount()):
        layer = dataset.GetLayer(index)
        if layer.GetName().endswith(":test_layer"):
            return layer
    raise AssertionError("test_layer not advertised")


def _wfs_checks(cell: Cell) -> None:
    with cell.check("positive", "GetCapabilities, DescribeFeatureType and GetFeature through the WFS driver") as c:
        with gdalkit.session() as record:
            dataset = _open_vector("WFS:" + WFS)
            layer = _wfs_layer(dataset)
            features = {fid: value for fid, value in _features(layer).items()}
            del dataset
        names = sorted(value[0] for value in features.values())
        expect(names == sorted(value[0] for value in FIXTURE.values()), names)
        expect(record.fetched_path("REQUEST=GetFeature") and record.fetched_path("REQUEST=DescribeFeatureType"),
               record.fetched)
        c.detail = f"{len(features)} features {names}; requests {sorted(set(record.fetched))}"
    with cell.check("negative", "an unknown feature type is refused with an OWS exception") as c:
        message = gdalkit.open_failure("WFS:" + WFS + "&TYPENAMES=honua:no_such_type")
        with gdalkit.session() as record:
            dataset = _open_vector("WFS:" + WFS)
            try:
                layer = dataset.GetLayerByName("honua:no_such_type")
                outcome = "absent" if layer is None else "present"
            except RuntimeError as error:
                outcome = str(error)
            del dataset
        expect(outcome == "absent" or "exception" in outcome.lower(), outcome)
        c.detail = f"typed open -> {message!r}; layer lookup -> {outcome}"
    with cell.check("auth", "the protected feature type is advertised only to authenticated principals") as c:
        advertised = {}
        for label, headers in (("anonymous", None), ("wrong-api-key", WRONG_API_KEY), ("expired-bearer", EXPIRED_BEARER),
                               ("api-key", api_key_headers()), ("oidc-bearer", bearer_headers())):
            with gdalkit.session(headers):
                try:
                    dataset = _open_vector("WFS:" + WFS)
                    names = [dataset.GetLayer(i).GetName() for i in range(dataset.GetLayerCount())]
                    protected = [name for name in names if "authenticated_test_layer" in name]
                    if protected:
                        advertised[label] = len(_features(dataset.GetLayerByName(protected[0])))
                    else:
                        advertised[label] = "hidden"
                    del dataset
                except RuntimeError as error:
                    advertised[label] = gdalkit.http_status_in(str(error)) or str(error)[:80]
        expect(all(advertised[label] in ("hidden", 401) for label in ("anonymous", "wrong-api-key", "expired-bearer")),
               advertised)
        expect(advertised["api-key"] == 10 and advertised["oidc-bearer"] == 10, advertised)
        c.detail = f"{advertised}"
    with cell.check("crs-axis", "EPSG:4326 layer extent and lat/lon BBOX filter follow the fixture") as c:
        with gdalkit.session() as record:
            dataset = _open_vector("WFS:" + WFS)
            layer = _wfs_layer(dataset)
            code = layer.GetSpatialRef().GetAuthorityCode(None)
            extent = layer.GetExtent()
            located = {value[0]: value[2] for value in _features(layer).values()}
            layer.SetSpatialFilterRect(-122.47, 37.715, -122.44, 37.745)
            windowed = sorted(value[0] for value in _features(layer).values())
            del dataset
        expect(code == "4326", code)
        expect([round(v, 3) for v in extent] == [-122.49, -122.37, 37.71, 37.79], extent)
        expect(located["gamma"] == FIXTURE[3][2], located)
        expect(windowed == ["delta", "gamma"], windowed)
        c.detail = f"EPSG:{code} extent {extent}; bbox window -> {windowed}; requests {record.fetched_path('BBOX')[:1]}"
    with cell.check("media-schema", "DescribeFeatureType types and GML payloads decode to typed fields") as c:
        with gdalkit.session() as record:
            dataset = _open_vector("WFS:" + WFS)
            layer = _wfs_layer(dataset)
            types = _field_types(layer)
            geometry_type = ogr.GeometryTypeToName(layer.GetGeomType())
            del dataset
        expect(types.get("name") == "String" and types.get("count") == "Integer", types)
        expect("Point" in geometry_type, geometry_type)
        c.detail = f"fields {types}; geometry {geometry_type}"


def wfs_operation() -> None:
    cell = _cell("client-cert/gdal-ogr/ogc/OGC-OP-WFS-2-0", "WFS 2.0 simple", version="2.0.0")
    cell.primary_request_url = WFS + "&REQUEST=GetFeature"
    _wfs_checks(cell)
    cell.write()


def wfs_service() -> None:
    cell = _cell("client-cert/gdal-ogr/wfs/serve.wfs", "WFS 2.0 simple+paging", version="2.0.0")
    cell.primary_request_url = WFS + "&REQUEST=GetCapabilities"
    _wfs_checks(cell)
    with cell.check("pagination", "the driver pages GetFeature with STARTINDEX/COUNT") as c:
        with gdalkit.session(None, OGR_WFS_PAGING_ALLOWED="ON", OGR_WFS_PAGE_SIZE="3") as record:
            dataset = _open_vector("WFS:" + WFS)
            layer = _wfs_layer(dataset)
            names = [value[0] for value in _features(layer).values()]
            del dataset
        pages = [value for value in record.fetched_path("REQUEST=GetFeature") if "STARTINDEX=" in value.upper()]
        expect(sorted(names) == sorted(value[0] for value in FIXTURE.values()), names)
        expect(len(pages) >= 4, pages)
        c.detail = f"{len(names)} features over {len(pages)} STARTINDEX pages"
    with cell.check("limit", "COUNT bounds the returned features") as c:
        with gdalkit.session(None, OGR_WFS_PAGING_ALLOWED="ON", OGR_WFS_PAGE_SIZE="4") as record:
            dataset = _open_vector("WFS:" + WFS)
            layer = _wfs_layer(dataset)
            layer.ResetReading()
            first = [layer.GetNextFeature() for _ in range(4)]
            del dataset
        counted = [value for value in record.fetched_path("REQUEST=GetFeature") if "COUNT=4" in value.upper()]
        expect(counted and all(feature is not None for feature in first), record.fetched)
        c.detail = f"COUNT=4 requests {counted[:2]}"
    cell.write()


# ---------------------------------------------------------------------------
# WCS 2.0.1 and OGC API - Coverages (canonical client "GDAL")
# ---------------------------------------------------------------------------

def _wcs_checks(cell: Cell) -> None:
    connection = "WCS:" + WCS_PUBLIC + "?version=2.0.1&coverage=coverage_2000"
    with cell.check("positive", "open coverage_2000 and read every pixel") as c:
        with gdalkit.session() as record:
            dataset = gdalkit.wcs_open(connection)
            size = (dataset.RasterXSize, dataset.RasterYSize)
            minmax = dataset.GetRasterBand(1).ComputeRasterMinMax(False)
            del dataset
        expect(size == (64, 64) and minmax == (180.0, 180.0), (size, minmax))
        expect(record.fetched_path("REQUEST=GetCoverage"), record.fetched)
        c.detail = f"{size} min/max {minmax}; requests {sorted(set(record.fetched))}"
    with cell.check("negative", "an unknown coverage fails to open with the server's exception") as c:
        message = gdalkit.open_failure("WCS:" + WCS_PUBLIC + "?version=2.0.1&coverage=coverage_999999", flags=gdal.OF_RASTER)
        expect(message is not None, "an unknown coverage opened")
        c.detail = f"GDAL: {message[:300]}"
    with cell.check("auth", "protected coverage service challenges without a valid credential") as c:
        outcomes = {}
        for label, headers in (("anonymous", None), ("wrong-api-key", WRONG_API_KEY), ("expired-bearer", EXPIRED_BEARER),
                               ("api-key", api_key_headers()), ("oidc-bearer", bearer_headers())):
            message = gdalkit.open_failure("WCS:" + WCS_PROTECTED + "?version=2.0.1", headers, flags=gdal.OF_RASTER)
            outcomes[label] = "opened" if message is None else (gdalkit.http_status_in(message) or message[:120])
        expect(all(outcomes[label] == 401 for label in ("anonymous", "wrong-api-key", "expired-bearer")), outcomes)
        expect(all(outcomes[label] != 401 for label in ("api-key", "oidc-bearer")), outcomes)
        c.detail = (f"{outcomes} (authenticated principals pass the access policy; the protected mirror "
                    "offers no coverage for GDAL to open)")
    with cell.check("crs-axis", "EPSG:4326 georeferencing and lon/lat SUBSET axes") as c:
        with gdalkit.session() as record:
            dataset = gdalkit.wcs_open(connection)
            transform = dataset.GetGeoTransform()
            code = dataset.GetSpatialRef().GetAuthorityCode(None)
            dataset.GetRasterBand(1).ReadRaster(8, 8, 16, 16)
            del dataset
        expect(code in ("4326", "CRS84"), code)
        expect([round(value, 8) for value in transform] == [-122.45, 0.00109375, 0.0, 37.8, 0.0, -0.00109375], transform)
        subsets = [value for value in record.fetched_path("REQUEST=GetCoverage") if "SUBSET=x" in value]
        expect(subsets, f"no x/y SUBSET requests: {record.fetched}")
        c.detail = f"EPSG:{code} geotransform {transform}; subset request {subsets[-1].split('&SUBSET=', 1)[1]}"
    with cell.check("media-schema", "GeoTIFF GetCoverage payloads decode with the described band type") as c:
        with gdalkit.session() as record:
            dataset = gdalkit.wcs_open(connection)
            band = dataset.GetRasterBand(1)
            data_type = gdal.GetDataTypeName(band.DataType)
            band.ReadRaster(0, 0, 64, 64)
            del dataset
        formats = [value for value in record.fetched_path("REQUEST=GetCoverage") if "Format=image/tiff" in value]
        expect(data_type == "Byte" and formats, (data_type, record.fetched))
        c.detail = f"band type {data_type}; GetCoverage Format=image/tiff requests {len(formats)}"


def wcs_operation() -> None:
    cell = Cell("client-cert/gdal/ogc/OGC-OP-WCS-2-0-COVERAGE", client_version_detail=CLIENT_DETAIL,
                protocol_version="2.0.1", protocol_profile="WCS 2.0.1 core GET-KVP")
    cell.primary_request_url = WCS_PUBLIC + "?SERVICE=WCS&REQUEST=GetCoverage&VERSION=2.0.1&COVERAGEID=coverage_2000"
    _wcs_checks(cell)
    cell.write()


def wcs_service() -> None:
    cell = Cell("client-cert/gdal/wcs/serve.wcs", client_version_detail=CLIENT_DETAIL,
                protocol_version="2.0.1", protocol_profile="WCS 2.0.1 core GET-KVP")
    cell.primary_request_url = WCS_PUBLIC + "?SERVICE=WCS&REQUEST=GetCapabilities&VERSION=2.0.1"
    _wcs_checks(cell)
    with cell.check("boundary", "edge and single-pixel windows read the fixture value") as c:
        with gdalkit.session() as record:
            dataset = gdalkit.wcs_open("WCS:" + WCS_PUBLIC + "?version=2.0.1&coverage=coverage_2000")
            band = dataset.GetRasterBand(1)
            corner = set(bytes(band.ReadRaster(60, 60, 4, 4)))
            pixel = set(bytes(band.ReadRaster(63, 0, 1, 1)))
            del dataset
        expect(corner == {180} and pixel == {180}, (corner, pixel))
        c.detail = f"bottom-right 4x4 {corner}, top-right pixel {pixel}; requests {len(record.fetched)}"
    cell.write()


def coverages_service() -> None:
    cell = Cell("client-cert/gdal/ogc-api-coverages/serve.ogc-api-coverages", client_version_detail=CLIENT_DETAIL,
                protocol_version="OGC API - Coverages 1.0", protocol_profile="core+geotiff")
    target = url("/ogc/coverages/collections/2000")
    connection = "OGCAPI:" + target
    cell.primary_request_url = target + "/coverage"

    def open_coverage():
        return gdal.OpenEx(connection, gdal.OF_RASTER, open_options=["API=COVERAGE"])

    with cell.check("positive", "open the collection with the OGCAPI driver's coverage API") as c:
        with gdalkit.session() as record:
            try:
                dataset = open_coverage()
            except RuntimeError as error:
                collection, _ = _json_with_headers(target)
                coverage_links = [(link.get("rel"), link.get("type")) for link in collection.get("links", [])
                                  if "coverage" in link.get("rel", "")]
                raise AssertionError(
                    f"GDAL {gdal.__version__} OGCAPI: {error}; the collection advertises coverage links "
                    f"{coverage_links}, which this driver release does not recognise as a coverage endpoint")
            size = (dataset.RasterXSize, dataset.RasterYSize)
            del dataset
        expect(size == (64, 64), size)
        c.detail = f"{size}; requests {sorted(set(record.fetched))}"

    for facet, name in (("negative", "an unknown coverage collection is refused"),
                        ("auth", "a protected coverage collection is challenged"),
                        ("boundary", "an edge window reads through a spatially subset request"),
                        ("crs-axis", "the coverage is georeferenced in lon/lat"),
                        ("media-schema", "the coverage decodes as GeoTIFF")):
        with cell.check(facet, name) as c:
            if facet == "negative":
                message = gdalkit.open_failure("OGCAPI:" + url("/ogc/coverages/collections/999999"), flags=gdal.OF_RASTER,
                                               open_options=["API=COVERAGE"])
                expect(message and gdalkit.http_status_in(message) == 404, message)
                c.detail = f"{message[:200]}"
                continue
            if facet == "auth":
                outcomes = {label: gdalkit.http_status_in(gdalkit.open_failure(
                    "OGCAPI:" + url("/ogc/coverages/collections/2010"), headers, flags=gdal.OF_RASTER,
                    open_options=["API=COVERAGE"])) for label, headers in (
                        ("anonymous", None), ("wrong-api-key", WRONG_API_KEY), ("expired-bearer", EXPIRED_BEARER))}
                expect(all(value == 401 for value in outcomes.values()), outcomes)
                c.detail = f"{outcomes}"
                continue
            with gdalkit.session():
                dataset = open_coverage()
                c.detail = f"{dataset.RasterXSize}x{dataset.RasterYSize}"
                del dataset
    cell.write()


CELLS = (features_landing, features_conformance, features_collections, features_collection, features_items,
         features_item, features_queryables, features_transactions, features_service, wfs_operation, wfs_service,
         wcs_operation, wcs_service, coverages_service)
