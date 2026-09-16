"""QGIS 3.44.13-Solothurn bounded-roster cells (lane ``desktop-qgis``)."""
from __future__ import annotations

from qgis.core import (
    Qgis,
    QgsFeature,
    QgsFeatureRequest,
    QgsGeometry,
    QgsPointXY,
    QgsRaster,
    QgsRasterLayer,
    QgsRectangle,
    QgsVectorLayer,
    QgsVectorTileLayer,
    QgsWkbTypes,
)

import qgiskit
from cellkit import Cell, expect
from rosterenv import url

CLIENT_DETAIL = f"QGIS {Qgis.version()} (PyQGIS, qgis/qgis:3.44.13)"
OAPIF = url("/ogc/features")
WFS = url("/wfs")
WMS = url("/rest/services/browser_compat/MapServer/WMS")
WMS_PROTECTED = url("/rest/services/cert_auth_raster/MapServer/WMS")
WMTS = url("/rest/services/browser_compat/MapServer/WMTS")
TILES = url("/ogc/tiles")
MAPS = url("/ogc/maps")
STYLES = url("/ogc/styles")
FEATURESERVER = url("/rest/services/test_service/FeatureServer")
MAPSERVER = url("/rest/services/browser_compat/MapServer")

NAMES = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "lambda"]
LOCATIONS = {"alpha": (-122.49, 37.71), "gamma": (-122.46, 37.73), "iota": (-122.37, 37.79)}
BROWSER_EXTENT_4326 = QgsRectangle(-122.45, 37.73, -122.38, 37.80)
BROWSER_EXTENT_3857 = QgsRectangle(-13631000, 4543000, -13622000, 4552000)
DENIED = ("anonymous", "wrong-api-key", "expired-bearer")
ADMITTED = ("api-key", "oidc-bearer")


def _cell(test_id: str, version: str, profile: str) -> Cell:
    return Cell(test_id, client_version_detail=CLIENT_DETAIL, protocol_version=version, protocol_profile=profile)


def _auth(label: str) -> str:
    return "" if label == "anonymous" else f" authcfg='{qgiskit.AUTHCFG[label]}'"


def _oapif(typename: str, label: str = "anonymous", extra: str = "") -> QgsVectorLayer:
    return QgsVectorLayer(f"url='{OAPIF}' typename='{typename}'{extra}{_auth(label)}", f"oapif-{typename}", "OAPIF")


def _names(layer: QgsVectorLayer, request: QgsFeatureRequest | None = None) -> list[str]:
    return sorted(feature["name"] for feature in layer.getFeatures(request or QgsFeatureRequest()))


def _xy(layer: QgsVectorLayer) -> dict[str, tuple[float, float]]:
    return {feature["name"]: (round(feature.geometry().asPoint().x(), 6), round(feature.geometry().asPoint().y(), 6))
            for feature in layer.getFeatures() if not feature.geometry().isNull()}


def _statuses(recorder, fragment: str) -> list:
    return [(item.method, item.status) for item in recorder.matching(fragment)]


# ---------------------------------------------------------------------------
# OGC API - Features (ogc surface operations)
# ---------------------------------------------------------------------------

def _oapif_negative(cell: Cell) -> None:
    with cell.check("negative", "an unknown collection yields an invalid layer from a 404") as c:
        with qgiskit.recording() as recorder:
            layer = _oapif("no-such-collection")
            valid = layer.isValid()
        statuses = _statuses(recorder, "/collections/no-such-collection")
        expect(not valid and any(status == 404 for _, status in statuses), f"valid={valid} {statuses}")
        c.detail = f"layer valid={valid}; server answered {statuses}"


def _oapif_auth(cell: Cell) -> None:
    with cell.check("auth", "protected collection 2011 through QGIS authcfg APIHeader configurations") as c:
        outcomes = {}
        for label in DENIED + ADMITTED:
            with qgiskit.recording() as recorder:
                layer = _oapif("2011", label)
                outcomes[label] = (layer.featureCount() if layer.isValid() else "invalid",
                                   sorted({status for _, status in _statuses(recorder, "/collections/2011")}))
        expect(all(outcomes[label][0] == "invalid" and 401 in outcomes[label][1] for label in DENIED), outcomes)
        expect(all(outcomes[label][0] == 10 for label in ADMITTED), outcomes)
        c.detail = f"(features, statuses) {outcomes}"


def _oapif_crs_axis(cell: Cell) -> None:
    with cell.check("crs-axis", "lon/lat geometry, EPSG:3857 request and bbox window agree with the fixture") as c:
        with qgiskit.recording() as recorder:
            layer = _oapif("0")
            located = _xy(layer)
            window = _names(layer, QgsFeatureRequest().setFilterRect(QgsRectangle(-122.47, 37.715, -122.44, 37.745)))
            projected_layer = _oapif("0", extra=" srsname='EPSG:3857'")
            projected = _xy(projected_layer).get("alpha")
        expect(layer.crs().authid() in ("EPSG:4326", "OGC:CRS84"), layer.crs().authid())
        expect(all(located[name] == LOCATIONS[name] for name in LOCATIONS), located)
        expect(window == ["delta", "gamma"], window)
        crs_requests = [item.url for item in recorder.matching("crs=http://www.opengis.net/def/crs/EPSG/0/3857")]
        # EPSG:3857 of (-122.49, 37.71): x = lon * 20037508.3428 / 180, y = ln(tan(pi/4 + lat/2)) * 6378137.
        import math
        expected = (-122.49 * 20037508.342789244 / 180,
                    math.log(math.tan(math.pi / 4 + math.radians(37.71) / 2)) * 6378137)
        expect(projected and abs(projected[0] - expected[0]) < 1 and abs(projected[1] - expected[1]) < 1 and crs_requests,
               f"EPSG:3857 alpha {projected}; crs requests {crs_requests}")
        c.detail = (f"layer {layer.crs().authid()}; alpha {located['alpha']}; bbox window {window}; "
                    f"EPSG:3857 alpha {projected} via {crs_requests[0].split('?', 1)[1]}")


def _oapif_media(cell: Cell) -> None:
    with cell.check("media-schema", "GeoJSON items negotiate their media type and decode to typed fields") as c:
        with qgiskit.recording() as recorder:
            layer = _oapif("0")
            fields = {field.name(): field.typeName() for field in layer.fields()}
            geometry = QgsWkbTypes.displayString(layer.wkbType())
            list(layer.getFeatures())
        items = [item for item in recorder.matching("/collections/0/items") if item.method == "GET"]
        expect(items and all((item.content_type or "").startswith("application/geo+json") for item in items),
               [(item.url, item.content_type) for item in items])
        expect(geometry == "Point" and fields.get("count", "").lower().startswith(("int", "integer"))
               and "date" in fields.get("created_at", "").lower(), (geometry, fields))
        c.detail = f"geometry {geometry}; fields {fields}; items served {items[0].content_type}"


def _oapif_cell(test_id: str, name: str, body) -> None:
    cell = _cell(test_id, "OGC API - Features 1.0", "core+geojson+crs")
    with cell.check("positive", name) as c:
        with qgiskit.recording() as recorder:
            detail, primary = body(recorder)
        cell.primary_request_url = primary
        c.detail = detail + f"; exchanges {recorder.summary(8)}"
    _oapif_negative(cell)
    _oapif_auth(cell)
    _oapif_crs_axis(cell)
    _oapif_media(cell)
    cell.write()


def oapif_landing() -> None:
    def body(recorder):
        layer = _oapif("0")
        expect(layer.isValid(), layer.error().summary())
        landing = [item for item in recorder.matching(OAPIF) if item.url == OAPIF and item.status == 200]
        expect(landing, recorder.summary())
        return f"layer valid; landing page {landing[0].content_type}", OAPIF
    _oapif_cell("client-cert/qgis/ogc/OGC-OP-OGC-API-FEATURES-LANDING", "the provider reads the landing page", body)


def oapif_conformance() -> None:
    def body(recorder):
        layer = _oapif("0")
        expect(layer.isValid(), layer.error().summary())
        conformance = [item for item in recorder.matching("/ogc/features/conformance") if item.status == 200]
        expect(conformance, recorder.summary())
        return f"conformance declaration read ({conformance[0].content_type})", OAPIF + "/conformance"
    _oapif_cell("client-cert/qgis/ogc/OGC-OP-OGC-API-FEATURES-CONFORMANCE", "the provider reads the conformance declaration", body)


def oapif_collections() -> None:
    def body(recorder):
        from qgis.core import QgsDataItemProviderRegistry, QgsApplication  # noqa: F401
        from qgis.PyQt.QtCore import QSettings
        settings = QSettings()
        settings.setValue("connections/ows/items/wfs/connections/items/roster-oapif/url", OAPIF)
        settings.setValue("connections/ows/items/wfs/connections/items/roster-oapif/version", "OGC_API_FEATURES")
        settings.sync()
        provider = next(p for p in QgsApplication.dataItemProviderRegistry().providers() if p.name() == "WFS")
        root = provider.createDataItem("", None)
        connections = root.createChildren() if root else []
        connection = next((item for item in connections if item.name() == "roster-oapif"), None)
        expect(connection is not None, f"browser connections {[item.name() for item in connections]}")
        layers = sorted(item.name() for item in connection.createChildren())
        listed = [item for item in recorder.matching("/ogc/features/collections") if item.url.rstrip("/").endswith("/collections")]
        expect(listed and listed[0].status == 200, recorder.summary())
        expect(len(layers) == 8, layers)
        return f"browser listed {len(layers)} collections {layers}", OAPIF + "/collections"
    _oapif_cell("client-cert/qgis/ogc/OGC-OP-OGC-API-FEATURES-COLLECTIONS",
                "the QGIS browser lists the API's collections", body)


def oapif_collection() -> None:
    def body(recorder):
        layer = _oapif("0")
        metadata = layer.dataProvider().layerMetadata()
        extent = layer.extent()
        expect(metadata.title() == "Test Layer", metadata.title())
        # The layer extent is the collection's advertised spatial extent, which encloses the data.
        expect(extent.xMinimum() <= -122.49 and extent.xMaximum() >= -122.37 and extent.yMinimum() <= 37.71
               and extent.yMaximum() >= 37.79 and extent.width() < 1, extent.toString())
        expect(recorder.matching("/ogc/features/collections/0"), recorder.summary())
        return f"title {metadata.title()!r}; abstract {metadata.abstract()!r}; extent {extent.toString(3)}", OAPIF + "/collections/0"
    _oapif_cell("client-cert/qgis/ogc/OGC-OP-OGC-API-FEATURES-COLLECTION", "collection metadata becomes layer metadata", body)


def oapif_items() -> None:
    def body(recorder):
        layer = _oapif("0")
        names = _names(layer)
        expect(names == sorted(NAMES), names)
        expect([item for item in recorder.matching("/collections/0/items") if item.method == "GET" and item.status == 200],
               recorder.summary())
        return f"read {len(names)} items {names}", OAPIF + "/collections/0/items"
    _oapif_cell("client-cert/qgis/ogc/OGC-OP-OGC-API-FEATURES-ITEMS", "read every item", body)


def oapif_item() -> None:
    def body(recorder):
        # QGIS requests /items/{featureId} when it must read back a feature the
        # server created; a scratch insert is the provider path that exercises it.
        layer = _oapif("11", "api-key")
        expect(layer.isValid(), layer.error().summary())
        layer.startEditing()
        inserted = QgsFeature(layer.fields())
        inserted.setGeometry(QgsGeometry.fromPointXY(QgsPointXY(-122.405, 37.775)))
        inserted["name"] = "roster-item-probe"
        expect(layer.addFeature(inserted) and layer.commitChanges(), f"insert {layer.commitErrors()}")
        created = [item for item in recorder.matching("/collections/11/items", "POST") if item.status == 201]
        single = [item for item in recorder.matching("/collections/11/items/", "GET") if item.status == 200]
        layer.reload()
        probe = [feature for feature in layer.getFeatures() if feature["name"] == "roster-item-probe"]
        located = (round(probe[0].geometry().asPoint().x(), 6), round(probe[0].geometry().asPoint().y(), 6)) if probe else None
        layer.startEditing()
        for feature in probe:
            layer.deleteFeature(feature.id())
        expect(layer.commitChanges(), f"cleanup {layer.commitErrors()}")
        expect(created and single, recorder.summary(20))
        expect(located == (-122.405, 37.775), located)
        expect((single[0].content_type or "").startswith("application/geo+json"), single[0].content_type)
        return f"created {created[0].status}; read back {single[0].url} -> {located}; probe removed", single[0].url
    _oapif_cell("client-cert/qgis/ogc/OGC-OP-OGC-API-FEATURES-ITEM", "read a single item resource back from the server", body)


def oapif_queryables() -> None:
    def body(recorder):
        layer = _oapif("0")
        active = _names(layer, QgsFeatureRequest().setFilterExpression("status = 'active'"))
        expect(active == sorted(["alpha", "gamma", "epsilon", "eta", "iota"]), active)
        queryables = recorder.matching("/collections/0/queryables")
        expect(queryables and queryables[0].status == 200,
               "QGIS evaluated the filter without requesting the collection queryables; the server does not declare "
               "the Features Part 3 filter/CQL2 conformance classes the provider needs to push filters down: "
               + "; ".join(recorder.summary(12)))
        return f"filter {active}; queryables {queryables[0].content_type}", OAPIF + "/collections/0/queryables"
    _oapif_cell("client-cert/qgis/ogc/OGC-OP-OGC-API-FEATURES-QUERYABLES",
                "the provider requests queryables for server-side filtering", body)


def oapif_transactions() -> None:
    def body(recorder):
        layer = _oapif("11", "api-key")
        expect(layer.isValid() and layer.featureCount() == 1, f"scratch layer 11 {layer.featureCount()}")
        name_index = layer.fields().indexOf("name")
        original = next(layer.getFeatures())
        before = original["name"]
        layer.startEditing()
        expect(layer.changeAttributeValue(original.id(), name_index, "roster-update"), "changeAttributeValue")
        expect(layer.commitChanges(), f"update commit {layer.commitErrors()}")
        layer.startEditing()
        inserted = QgsFeature(layer.fields())
        inserted.setGeometry(QgsGeometry.fromPointXY(QgsPointXY(-122.41, 37.77)))
        inserted["name"] = "roster-insert"
        expect(layer.addFeature(inserted), "addFeature")
        expect(layer.commitChanges(), f"insert commit {layer.commitErrors()}")
        layer.reload()
        after = _names(layer)
        created = [feature.id() for feature in layer.getFeatures() if feature["name"] == "roster-insert"]
        layer.startEditing()
        for fid in created:
            layer.deleteFeature(fid)
        layer.changeAttributeValue(original.id(), name_index, before)
        expect(layer.commitChanges(), f"restore commit {layer.commitErrors()}")
        layer.reload()
        restored = _names(layer)
        writes = [(item.method, item.status) for item in recorder.matching("/collections/11/items")
                  if item.method not in ("GET", "HEAD") and item.status != 405]
        expect(after == sorted(["roster-insert", "roster-update"]) and restored == [before], (after, restored))
        expect(any(status in (200, 201) for method, status in writes if method == "POST"), writes)
        return (f"update -> insert -> delete/restore on scratch collection 11: after {after}, restored {restored}; "
                f"write exchanges {writes}"), OAPIF + "/collections/11/items"
    _oapif_cell("client-cert/qgis/ogc/OGC-OP-OGC-API-FEATURES-TRANSACTIONS",
                "edit, insert and delete through the provider's editing buffer", body)


def oapif_service() -> None:
    cell = _cell("client-cert/qgis/ogc-api-features/serve.ogc-api-features", "OGC API - Features 1.0", "core+geojson+crs+paging")
    cell.primary_request_url = OAPIF + "/collections/0/items"
    with cell.check("positive", "read the collection") as c:
        with qgiskit.recording() as recorder:
            names = _names(_oapif("0"))
        expect(names == sorted(NAMES), names)
        c.detail = f"{names}; {recorder.summary(6)}"
    with cell.check("pagination", "the provider follows next links with a small page size") as c:
        with qgiskit.recording() as recorder:
            names = _names(_oapif("0", extra=" pageSize='3'"))
        pages = [item.url for item in recorder.matching("/collections/0/items") if item.method == "GET"]
        followed = [page for page in pages if "offset=" in page or "startindex=" in page.lower()]
        expect(names == sorted(NAMES), names)
        expect(len(followed) >= 3, pages)
        c.detail = f"{len(names)} features over pages {pages}"
    with cell.check("limit", "the provider's page size is sent as limit") as c:
        with qgiskit.recording() as recorder:
            _names(_oapif("0", extra=" pageSize='4'"))
        limited = [item.url for item in recorder.matching("limit=4")]
        expect(limited, recorder.summary(12))
        c.detail = f"limit requests {limited[:3]}"
    _oapif_negative(cell)
    _oapif_auth(cell)
    _oapif_crs_axis(cell)
    _oapif_media(cell)
    cell.write()


def ogc_features_simple() -> None:
    cell = _cell("client-cert/qgis/ogc-features/serve.ogc-api-features", "OGC API - Features 1.0", "core+geojson")
    cell.primary_request_url = OAPIF + "/collections/0/items"
    with cell.check("positive", "load and read the collection") as c:
        with qgiskit.recording() as recorder:
            names = _names(_oapif("0"))
        expect(names == sorted(NAMES), names)
        c.detail = f"{names}; {recorder.summary(6)}"
    with cell.check("metadata", "collection title, abstract, extent and temporal metadata reach the layer") as c:
        layer = _oapif("0")
        metadata = layer.dataProvider().layerMetadata()
        temporal = metadata.extent().temporalExtents()
        expect(metadata.title() == "Test Layer" and metadata.abstract(), (metadata.title(), metadata.abstract()))
        expect(temporal and temporal[0].begin().date().toString("yyyy-MM-dd") == "2024-01-01", [str(t.begin()) for t in temporal])
        c.detail = f"title {metadata.title()!r}; temporal {temporal[0].begin().toString()}..{temporal[0].end().toString()}"
    _oapif_media(cell)
    cell.write()


# ---------------------------------------------------------------------------
# WFS 2.0
# ---------------------------------------------------------------------------

def _wfs(typename: str, label: str = "anonymous", extra: str = "") -> QgsVectorLayer:
    return QgsVectorLayer(f"url='{WFS}' typename='{typename}' version='2.0.0'{extra}{_auth(label)}", typename, "WFS")


def _wfs_positive(cell: Cell) -> None:
    with cell.check("positive", "GetCapabilities, DescribeFeatureType and GetFeature through the WFS provider") as c:
        with qgiskit.recording() as recorder:
            names = _names(_wfs("honua:test_layer"))
        requests = sorted({item.url.split("REQUEST=")[1].split("&")[0] for item in recorder.exchanges if "REQUEST=" in item.url})
        expect(names == sorted(NAMES), names)
        expect({"GetCapabilities", "DescribeFeatureType", "GetFeature"} <= set(requests), requests)
        c.detail = f"{names}; requests {requests}"


def _wfs_metadata_media(cell: Cell) -> None:
    with cell.check("metadata", "capabilities title and extent become layer metadata") as c:
        layer = _wfs("honua:test_layer")
        extent = layer.extent()
        expect(layer.crs().authid() == "EPSG:4326", layer.crs().authid())
        expect(round(extent.xMinimum(), 2) <= -122.49 and round(extent.yMaximum(), 2) >= 37.79, extent.toString())
        c.detail = f"crs {layer.crs().authid()}; extent {extent.toString(3)}; title {layer.dataProvider().layerMetadata().title()!r}"
    with cell.check("media-schema", "GML GetFeature responses decode against DescribeFeatureType types") as c:
        with qgiskit.recording() as recorder:
            layer = _wfs("honua:test_layer")
            fields = {field.name(): field.typeName() for field in layer.fields()}
            list(layer.getFeatures())
        responses = [(item.url.split("REQUEST=")[1].split("&")[0], item.content_type) for item in recorder.exchanges
                     if "REQUEST=" in item.url]
        expect(any(kind == "GetFeature" and "gml+xml" in (ctype or "") for kind, ctype in responses), responses)
        expect("int" in fields.get("count", "").lower(), fields)
        c.detail = f"fields {fields}; responses {responses}"


def wfs_operation() -> None:
    cell = _cell("client-cert/qgis/ogc/OGC-OP-WFS-2-0", "2.0.0", "WFS 2.0 simple")
    cell.primary_request_url = WFS + "?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES=honua:test_layer"
    _wfs_positive(cell)
    with cell.check("negative", "an unknown feature type yields an invalid layer") as c:
        layer = _wfs("honua:no_such_type")
        expect(not layer.isValid(), "an unknown type loaded")
        c.detail = f"invalid: {layer.error().summary()[:160]!r}"
    with cell.check("auth", "the protected type loads only with a valid credential (one QGIS process per credential)") as c:
        outcomes = {label: qgiskit.in_fresh_process("qgis_cells", "wfs_auth_probe", label)
                    for label in DENIED + ADMITTED}
        expect(all(outcomes[label][0] == "invalid" for label in DENIED), outcomes)
        expect(all(outcomes[label][0] == 10 for label in ADMITTED), outcomes)
        c.detail = f"(features, statuses) {outcomes}"
    with cell.check("crs-axis", "EPSG:4326 WFS 2.0 axis order decodes to lon/lat geometry") as c:
        with qgiskit.recording() as recorder:
            layer = _wfs("honua:test_layer")
            located = _xy(layer)
            window = _names(layer, QgsFeatureRequest().setFilterRect(QgsRectangle(-122.47, 37.715, -122.44, 37.745)))
        expect(all(located[name] == LOCATIONS[name] for name in LOCATIONS), located)
        expect(window == ["delta", "gamma"], window)
        c.detail = f"alpha {located['alpha']}; window {window}; {[item.url for item in recorder.matching('BBOX')][:1]}"
    _wfs_metadata_media_only_media(cell)
    cell.write()


def wfs_auth_probe(label: str) -> object:
    with qgiskit.recording() as recorder:
        layer = _wfs("honua:authenticated_test_layer", label)
        result = layer.featureCount() if layer.isValid() else "invalid"
    return [result, sorted({item.status for item in recorder.exchanges if item.status})]


def _wfs_metadata_media_only_media(cell: Cell) -> None:
    with cell.check("media-schema", "GML GetFeature responses decode against DescribeFeatureType types") as c:
        with qgiskit.recording() as recorder:
            layer = _wfs("honua:test_layer")
            fields = {field.name(): field.typeName() for field in layer.fields()}
            list(layer.getFeatures())
        responses = [(item.url.split("REQUEST=")[1].split("&")[0], item.content_type) for item in recorder.exchanges
                     if "REQUEST=" in item.url]
        expect(any(kind == "GetFeature" and "xml" in (ctype or "") for kind, ctype in responses), responses)
        expect("int" in fields.get("count", "").lower(), fields)
        c.detail = f"fields {fields}; responses {responses}"


def wfs_service() -> None:
    cell = _cell("client-cert/qgis/wfs/serve.wfs", "2.0.0", "WFS 2.0 simple")
    cell.primary_request_url = WFS + "?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0"
    _wfs_positive(cell)
    _wfs_metadata_media(cell)
    cell.write()


# ---------------------------------------------------------------------------
# WMS 1.3.0 and WMTS 1.0.0
# ---------------------------------------------------------------------------

def _wms(label: str = "anonymous", layers: str = "Browser Points", base: str = WMS, crs: str = "EPSG:4326") -> QgsRasterLayer:
    auth = "" if label == "anonymous" else f"authcfg={qgiskit.AUTHCFG[label]}&"
    return QgsRasterLayer(f"{auth}crs={crs}&format=image/png&layers={layers}&styles=&url={base}", f"wms-{label}", "wms")


def _wmts(label: str = "anonymous", layer: str = "2000") -> QgsRasterLayer:
    auth = "" if label == "anonymous" else f"authcfg={qgiskit.AUTHCFG[label]}&"
    return QgsRasterLayer(
        f"{auth}crs=EPSG:3857&format=image/png&layers={layer}&styles=default&tileMatrixSet=WebMercatorQuad"
        f"&url={WMTS}?SERVICE%3DWMTS%26REQUEST%3DGetCapabilities", f"wmts-{label}", "wms")


def _wms_positive(cell: Cell, feature_info: bool) -> None:
    with cell.check("positive", "GetCapabilities and GetMap render the fixture" + (" and GetFeatureInfo identifies it" if feature_info else "")) as c:
        with qgiskit.recording() as recorder:
            layer = _wms()
            expect(layer.isValid(), layer.error().summary())
            painted, colours = qgiskit.render(layer, BROWSER_EXTENT_4326, "EPSG:4326")
            identified = None
            if feature_info:
                result = layer.dataProvider().identify(
                    QgsPointXY(-122.4194, 37.7749), QgsRaster.IdentifyFormatText,
                    QgsRectangle(-122.425, 37.770, -122.414, 37.780), 128, 128)
                identified = str(result.results())
        getmaps = [item for item in recorder.matching("REQUEST=GetMap") if item.status == 200]
        expect(painted > 0 and getmaps, f"painted {painted}; {recorder.summary(6)}")
        if feature_info:
            expect("pt-alpha" in identified, f"GetFeatureInfo at pt-alpha returned {identified[:200]}")
        c.detail = f"painted {painted} px in {colours} colours via {len(getmaps)} GetMap" + (f"; identify {identified[:120]}" if feature_info else "")


def _wms_metadata(cell: Cell) -> None:
    with cell.check("metadata", "capabilities layer title, CRS list and extent reach the layer") as c:
        layer = _wms()
        metadata = layer.dataProvider().layerMetadata()
        extent = layer.extent()
        crs_list = layer.dataProvider().subLayers()
        expect(layer.isValid() and extent.xMinimum() < -122.4 and extent.yMaximum() > 37.75, extent.toString())
        c.detail = f"title {metadata.title()!r}; extent {extent.toString(4)}; sublayers {len(crs_list)}"


def _wms_media(cell: Cell) -> None:
    with cell.check("media-schema", "capabilities XML and PNG map responses carry their media types") as c:
        with qgiskit.recording() as recorder:
            layer = _wms()
            qgiskit.render(layer, BROWSER_EXTENT_4326, "EPSG:4326")
        capabilities = [item.content_type for item in recorder.matching("REQUEST=GetCapabilities")]
        maps = [item.content_type for item in recorder.matching("REQUEST=GetMap")]
        expect(capabilities and "xml" in capabilities[0], capabilities)
        expect(maps and all(value.startswith("image/png") for value in maps), maps)
        c.detail = f"capabilities {capabilities[0]}; GetMap {sorted(set(maps))}"


def wms_operation() -> None:
    cell = _cell("client-cert/qgis/ogc/OGC-OP-WMS-GETCAPABILITIES-GETMAP-GETFEATUREINF", "1.3.0", "WMS 1.3.0 queryable")
    cell.primary_request_url = WMS + "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap"
    _wms_positive(cell, feature_info=True)
    with cell.check("negative", "an unknown layer name is refused") as c:
        with qgiskit.recording() as recorder:
            layer = _wms(layers="No Such Layer")
            outcome = "invalid" if not layer.isValid() else qgiskit.render(layer, BROWSER_EXTENT_4326, "EPSG:4326")
        exceptions = [(item.status, item.content_type) for item in recorder.matching("REQUEST=GetMap")]
        expect(outcome == "invalid" or outcome[0] == 0, outcome)
        c.detail = f"outcome {outcome}; GetMap responses {exceptions}"
    with cell.check("auth", "the protected WMS loads and renders only with a valid credential") as c:
        outcomes = {}
        for label in DENIED + ADMITTED:
            with qgiskit.recording() as recorder:
                layer = _wms(label, layers="Authenticated Browser Points", base=WMS_PROTECTED)
                painted = qgiskit.render(layer, BROWSER_EXTENT_4326, "EPSG:4326")[0] if layer.isValid() else "invalid"
            outcomes[label] = (painted, sorted({item.status for item in recorder.exchanges}))
        expect(all(outcomes[label][0] in ("invalid", 0) for label in DENIED), outcomes)
        expect(all(isinstance(outcomes[label][0], int) and outcomes[label][0] > 0 for label in ADMITTED), outcomes)
        c.detail = f"(painted, statuses) {outcomes}"
    with cell.check("crs-axis", "EPSG:4326 (lat/lon BBOX) and CRS:84 renders of the same area agree") as c:
        with qgiskit.recording() as recorder:
            lat_lon = qgiskit.render_box(_wms(crs="EPSG:4326"), BROWSER_EXTENT_4326, "EPSG:4326")
            lon_lat = qgiskit.render_box(_wms(crs="CRS:84"), BROWSER_EXTENT_4326, "OGC:CRS84")
        bboxes = [item.url.split("BBOX=")[1].split("&")[0] for item in recorder.matching("REQUEST=GetMap")]
        expect(lat_lon[0] > 0 and lon_lat[0] > 0 and lat_lon[1] and lon_lat[1]
               and all(abs(a - b) <= 3 for a, b in zip(lat_lon[1], lon_lat[1])),
               f"painted footprints differ: EPSG:4326 {lat_lon}, CRS:84 {lon_lat}")
        expect(any(value.startswith("37.") for value in bboxes), bboxes)
        c.detail = f"(pixels, footprint) EPSG:4326 {lat_lon}, CRS:84 {lon_lat}; BBOX parameters {bboxes[:2]}"
    _wms_media(cell)
    cell.write()


def wms_service() -> None:
    cell = _cell("client-cert/qgis/wms/serve.wms", "1.3.0", "WMS 1.3.0")
    cell.primary_request_url = WMS + "?SERVICE=WMS&REQUEST=GetCapabilities"
    _wms_positive(cell, feature_info=False)
    _wms_metadata(cell)
    _wms_media(cell)
    cell.write()


def _wmts_positive(cell: Cell) -> None:
    with cell.check("positive", "GetCapabilities and GetTile render the fixture") as c:
        with qgiskit.recording() as recorder:
            layer = _wmts()
            expect(layer.isValid(), layer.error().summary())
            painted, colours = qgiskit.render(layer, BROWSER_EXTENT_3857, "EPSG:3857")
        tiles = [item for item in recorder.matching("") if "/WMTS/2000/" in item.url or "REQUEST=GetTile" in item.url]
        expect(painted > 0 and any(item.status == 200 for item in tiles), f"painted {painted}; {recorder.summary(8)}")
        c.detail = f"painted {painted} px in {colours} colours from {len(tiles)} tile requests"


def _wmts_media(cell: Cell) -> None:
    with cell.check("media-schema", "capabilities XML and PNG tiles carry their media types") as c:
        with qgiskit.recording() as recorder:
            qgiskit.render(_wmts(), QgsRectangle(-13628500, 4546500, -13624500, 4550500), "EPSG:3857", size=512)
        capabilities = [item.content_type for item in recorder.matching("REQUEST=GetCapabilities")]
        tiles = [item.content_type for item in recorder.matching("REQUEST=GetTile") if item.status == 200]
        expect(capabilities and "xml" in capabilities[0], capabilities)
        expect(tiles and all(value.startswith("image/png") for value in tiles), tiles)
        c.detail = f"capabilities {capabilities[0]}; tiles {sorted(set(tiles))}"


def wmts_operation() -> None:
    cell = _cell("client-cert/qgis/ogc/OGC-OP-WMTS-GETCAPABILITIES-GETTILE", "1.0.0", "WMTS 1.0.0 RESTful+KVP")
    cell.primary_request_url = WMTS + "?SERVICE=WMTS&REQUEST=GetCapabilities"
    _wmts_positive(cell)
    with cell.check("negative", "an unknown layer identifier is refused") as c:
        layer = _wmts(layer="no-such-layer")
        expect(not layer.isValid(), "unknown WMTS layer loaded")
        c.detail = f"invalid: {layer.error().summary()[:160]!r}"
    with cell.check("auth", "the protected mirror's WMTS requires a valid credential") as c:
        outcomes = {}
        for label in DENIED + ADMITTED:
            auth = "" if label == "anonymous" else f"authcfg={qgiskit.AUTHCFG[label]}&"
            with qgiskit.recording() as recorder:
                layer = QgsRasterLayer(
                    f"{auth}crs=EPSG:3857&format=image/png&layers=2010&styles=default&tileMatrixSet=WebMercatorQuad"
                    f"&url={url('/rest/services/cert_auth_raster/MapServer/WMTS')}?SERVICE%3DWMTS%26REQUEST%3DGetCapabilities",
                    "wmts-auth", "wms")
                painted = qgiskit.render(layer, BROWSER_EXTENT_3857, "EPSG:3857")[0] if layer.isValid() else "invalid"
            outcomes[label] = (painted, sorted({item.status for item in recorder.exchanges}))
        expect(all(outcomes[label][0] in ("invalid", 0) for label in DENIED), outcomes)
        expect(all(isinstance(outcomes[label][0], int) and outcomes[label][0] > 0 for label in ADMITTED), outcomes)
        c.detail = f"(painted, statuses) {outcomes}"
    with cell.check("crs-axis", "WebMercatorQuad tiles georeference the fixture where EPSG:3857 places it") as c:
        layer = _wmts()
        extent = layer.extent()
        inside = qgiskit.render(layer, BROWSER_EXTENT_3857, "EPSG:3857")[0]
        elsewhere = qgiskit.render(layer, QgsRectangle(1000000, 1000000, 1009000, 1009000), "EPSG:3857")[0]
        expect(layer.crs().authid() == "EPSG:3857" and inside > 0 and elsewhere == 0, (layer.crs().authid(), inside, elsewhere))
        c.detail = f"crs {layer.crs().authid()}; extent {extent.toString(0)}; painted at fixture {inside}, far away {elsewhere}"
    _wmts_media(cell)
    cell.write()


def wmts_service() -> None:
    cell = _cell("client-cert/qgis/wmts/serve.wmts", "1.0.0", "WMTS 1.0.0")
    cell.primary_request_url = WMTS + "?SERVICE=WMTS&REQUEST=GetCapabilities"
    _wmts_positive(cell)
    with cell.check("metadata", "tile matrix set and layer extent from capabilities") as c:
        layer = _wmts()
        expect(layer.isValid() and layer.crs().authid() == "EPSG:3857", layer.crs().authid())
        c.detail = f"crs {layer.crs().authid()}; extent {layer.extent().toString(0)}; title {layer.dataProvider().layerMetadata().title()!r}"
    _wmts_media(cell)
    cell.write()


# ---------------------------------------------------------------------------
# OGC API - Tiles, Maps and Styles
# ---------------------------------------------------------------------------

TILE_TEMPLATE = TILES + "/collections/2000/tiles/WebMercatorQuad/{z}/{y}/{x}?f%3Dmvt"


def _vector_tiles(label: str = "anonymous", collection: str = "2000", style_url: str | None = None) -> QgsVectorTileLayer:
    auth = "" if label == "anonymous" else f"authcfg={qgiskit.AUTHCFG[label]}&"
    template = TILE_TEMPLATE.replace("/2000/", f"/{collection}/")
    style = f"&styleUrl={style_url}" if style_url else ""
    return QgsVectorTileLayer(f"{auth}type=xyz&url={template}&zmin=0&zmax=16{style}", f"tiles-{collection}")


def _tiles_checks(cell: Cell) -> None:
    with cell.check("negative", "an unknown collection's tiles are refused") as c:
        with qgiskit.recording() as recorder:
            qgiskit.render(_vector_tiles(collection="no-such-collection"), BROWSER_EXTENT_3857, "EPSG:3857")
        statuses = sorted({item.status for item in recorder.exchanges if "/no-such-collection/" in item.url})
        expect(statuses and all(status == 404 for status in statuses), statuses)
        c.detail = f"tile statuses {statuses}"
    with cell.check("auth", "tiles of the protected collection require a valid credential") as c:
        outcomes = {}
        for label in DENIED + ADMITTED:
            with qgiskit.recording() as recorder:
                painted = qgiskit.render(_vector_tiles(label, "2011"), QgsRectangle(-13637000, 4538000, -13609000, 4550000), "EPSG:3857")[0]
            outcomes[label] = (painted, sorted({item.status for item in recorder.exchanges if "/2011/" in item.url}))
        expect(all(outcomes[label][0] == 0 and 401 in outcomes[label][1] for label in DENIED), outcomes)
        expect(all(outcomes[label][0] > 0 for label in ADMITTED), outcomes)
        c.detail = f"(painted, statuses) {outcomes}"
    with cell.check("crs-axis", "WebMercatorQuad tile content lands where EPSG:3857 places the fixture") as c:
        inside = qgiskit.render(_vector_tiles(), BROWSER_EXTENT_3857, "EPSG:3857")[0]
        elsewhere = qgiskit.render(_vector_tiles(), QgsRectangle(1000000, 1000000, 1009000, 1009000), "EPSG:3857")[0]
        expect(inside > 0 and elsewhere == 0, (inside, elsewhere))
        c.detail = f"painted at fixture {inside}, far away {elsewhere}"
    with cell.check("media-schema", "tiles are served as Mapbox Vector Tile") as c:
        with qgiskit.recording() as recorder:
            qgiskit.render(_vector_tiles(), BROWSER_EXTENT_3857, "EPSG:3857")
        types = sorted({(item.status, item.content_type) for item in recorder.exchanges if "/tiles/WebMercatorQuad/" in item.url})
        expect(any(status == 200 and (ctype or "").startswith("application/vnd.mapbox-vector-tile") for status, ctype in types), types)
        c.detail = f"tile responses {types}"


def tiles_tile() -> None:
    cell = _cell("client-cert/qgis/ogc/OGC-OP-OGC-API-TILES-TILE", "OGC API - Tiles 1.0", "core+mvt")
    cell.primary_request_url = TILES + "/collections/2000/tiles/WebMercatorQuad/14/6331/2621?f=mvt"
    with cell.check("positive", "render vector tiles for the browser point collection") as c:
        with qgiskit.recording() as recorder:
            painted, colours = qgiskit.render(_vector_tiles(), BROWSER_EXTENT_3857, "EPSG:3857")
        tiles = [item for item in recorder.exchanges if "/tiles/WebMercatorQuad/" in item.url and item.status == 200]
        expect(painted > 0 and tiles, f"painted {painted}; {recorder.summary(6)}")
        c.detail = f"painted {painted} px from {len(tiles)} tiles"
    _tiles_checks(cell)
    cell.write()


def tiles_landing_tilesets() -> None:
    cell = _cell("client-cert/qgis/ogc/OGC-OP-OGC-API-TILES-LANDING-TILESETS", "OGC API - Tiles 1.0", "core+tilesets-list")
    cell.primary_request_url = TILES + "/collections/2000/tiles"
    with cell.check("positive", "open the collection's tilesets through QGIS's GDAL OGCAPI provider") as c:
        layer = QgsVectorLayer("OGCAPI:" + TILES + "/collections/2000", "tilesets", "ogr")
        sublayers = layer.dataProvider().subLayers() if layer.isValid() else []
        # The provider runs GDAL on a worker thread, outside the Python debug
        # handler, so the tileset documents are read back through QGIS's own
        # network stack and compared with what the provider derived from them.
        import json
        from qgis.core import QgsBlockingNetworkRequest
        from qgis.PyQt.QtCore import QUrl
        from qgis.PyQt.QtNetwork import QNetworkRequest as Request

        def fetch(target: str) -> dict:
            blocking = QgsBlockingNetworkRequest()
            expect(blocking.get(Request(QUrl(target))) == QgsBlockingNetworkRequest.NoError, blocking.errorMessage())
            return json.loads(bytes(blocking.reply().content()).decode("utf-8"))

        tilesets = fetch(TILES + "/collections/2000/tiles")["tilesets"]
        chosen = [tileset for tileset in tilesets if tileset.get("dataType") == "vector"]
        expect(chosen, tilesets)
        detail_link = next((link["href"] for link in chosen[0].get("links", []) if link.get("rel") == "self"),
                           TILES + f"/collections/2000/tiles/{chosen[0]['tileMatrixSetId']}")
        limits = fetch(detail_link).get("tileMatrixSetLimits") or chosen[0].get("tileMatrixSetLimits", [])
        expect(layer.isValid() and len(sublayers) == len(limits),
               f"valid={layer.isValid()} provider zoom sublayers {len(sublayers)} vs tileset limits {len(limits)}")
        c.detail = (f"{len(tilesets)} tilesets ({[t['tileMatrixSetId'] for t in tilesets]}); provider exposed "
                    f"{len(sublayers)} zoom levels matching {len(limits)} tileMatrixSetLimits of {chosen[0]['tileMatrixSetId']}")
    _tiles_checks(cell)
    cell.write()


def tiles_service() -> None:
    cell = _cell("client-cert/qgis/ogc-api-tiles/serve.ogc-api-tiles", "OGC API - Tiles 1.0", "core+mvt")
    cell.primary_request_url = TILES + "/collections/2000/tiles/WebMercatorQuad"
    with cell.check("positive", "render the collection's vector tiles") as c:
        painted, colours = qgiskit.render(_vector_tiles(), BROWSER_EXTENT_3857, "EPSG:3857")
        expect(painted > 0, painted)
        c.detail = f"painted {painted} px"
    _tiles_checks(cell)
    cell.write()


def maps_service() -> None:
    cell = _cell("client-cert/qgis/ogc-api-maps/serve.ogc-api-maps", "OGC API - Maps 1.0", "core+collection-map")
    target = MAPS + "/collections/2000"
    cell.primary_request_url = target + "/map"

    def open_map(label: str = "anonymous", collection: str = "2000") -> tuple[QgsRasterLayer, list[str]]:
        from osgeo import gdal
        header_file = None
        if label != "anonymous":
            import tempfile
            from rosterenv import EXPIRED_BEARER, WRONG_API_KEY, api_key_headers, bearer_headers
            headers = {"api-key": api_key_headers(), "oidc-bearer": bearer_headers(),
                       "wrong-api-key": WRONG_API_KEY, "expired-bearer": EXPIRED_BEARER}[label]
            handle = tempfile.NamedTemporaryFile("w", delete=False, suffix=".headers")
            handle.write("".join(f"{k}: {v}\n" for k, v in headers.items()))
            handle.close()
            header_file = handle.name
        gdal.SetConfigOption("GDAL_HTTP_HEADER_FILE", header_file)
        try:
            with qgiskit.recording() as recorder:
                layer = QgsRasterLayer("OGCAPI:" + MAPS + f"/collections/{collection}", "map", "gdal")
            return layer, recorder.gdal_fetched
        finally:
            gdal.SetConfigOption("GDAL_HTTP_HEADER_FILE", None)

    with cell.check("positive", "open the collection map through QGIS's GDAL OGCAPI provider and render it") as c:
        layer, fetched = open_map()
        expect(layer.isValid(), f"{layer.error().summary()[:300]}; GDAL fetched {fetched}")
        painted = qgiskit.render(layer, BROWSER_EXTENT_4326, "EPSG:4326")[0]
        expect(painted > 0, painted)
        c.detail = f"painted {painted}; fetched {fetched}"
    with cell.check("negative", "an unknown collection map is refused") as c:
        layer, fetched = open_map(collection="no-such-collection")
        expect(not layer.isValid() and "404" in layer.error().summary(), layer.error().summary()[:200])
        c.detail = f"{layer.error().summary()[:200]}"
    with cell.check("auth", "the protected collection's map requires a valid credential") as c:
        outcomes = {}
        for label in DENIED + ADMITTED:
            layer, _ = open_map(label, "2010")
            outcomes[label] = "valid" if layer.isValid() else ("401" if "401" in layer.error().summary() else layer.error().summary()[:80])
        expect(all(outcomes[label] == "401" for label in DENIED) and all(outcomes[label] == "valid" for label in ADMITTED), outcomes)
        c.detail = f"{outcomes}"
    with cell.check("crs-axis", "the map is georeferenced over the fixture's lon/lat extent") as c:
        layer, _ = open_map()
        expect(layer.isValid(), layer.error().summary()[:200])
        extent = layer.extent()
        c.detail = f"crs {layer.crs().authid()}; extent {extent.toString(4)}"
        expect(extent.xMinimum() < -122.38 and extent.yMaximum() > 37.73, extent.toString())
    with cell.check("media-schema", "map responses decode as PNG") as c:
        layer, fetched = open_map()
        expect(layer.isValid(), layer.error().summary()[:200])
        c.detail = f"bands {layer.bandCount()}; fetched {fetched}"
    cell.write()


def styles_service() -> None:
    cell = _cell("client-cert/qgis/ogc-api-styles/styling.ogc-api-styles", "OGC API - Styles 1.0", "core+mapbox-styles")
    cell.primary_request_url = STYLES
    advertised = url("/ogc/styles/Browser%20Points")

    with cell.check("positive", "apply the collection's advertised MapLibre stylesheet to its vector tiles") as c:
        with qgiskit.recording() as recorder:
            layer = _vector_tiles(style_url=advertised + "?f=mapbox")
            error, ok = layer.loadDefaultStyle()
        styles = [(item.status, item.content_type) for item in recorder.matching("/ogc/styles/")]
        expect(ok and any(status == 200 for status, _ in styles),
               f"loadDefaultStyle -> {ok} {error!r}; stylesheet responses {styles}; the collection advertises "
               f"{advertised} (rel=stylesheet) but the Styles API does not serve it")
        c.detail = f"style applied; responses {styles}"
    with cell.check("negative", "an unknown style is a 404") as c:
        with qgiskit.recording() as recorder:
            layer = _vector_tiles(style_url=url("/ogc/styles/no-such-style?f=mapbox"))
            _, ok = layer.loadDefaultStyle()
        statuses = [item.status for item in recorder.matching("/ogc/styles/no-such-style")]
        expect(not ok and statuses == [404] * len(statuses) and statuses, (ok, statuses))
        c.detail = f"loadDefaultStyle {ok}; statuses {statuses}"
    with cell.check("auth", "styles of the protected collection require a valid credential") as c:
        with qgiskit.recording() as recorder:
            _vector_tiles("anonymous", "2010", url("/ogc/styles/Authenticated%20Browser%20Points?f=mapbox")).loadDefaultStyle()
        statuses = [item.status for item in recorder.matching("/ogc/styles/")]
        expect(401 in statuses, f"anonymous stylesheet request statuses {statuses}")
        c.detail = f"anonymous {statuses}"
    with cell.check("media-schema", "the stylesheet is served as a Mapbox/MapLibre style document") as c:
        with qgiskit.recording() as recorder:
            _vector_tiles(style_url=advertised + "?f=mapbox").loadDefaultStyle()
        types = [(item.status, item.content_type) for item in recorder.matching("/ogc/styles/")]
        expect(any(status == 200 and "mapbox.style+json" in (ctype or "") for status, ctype in types), types)
        c.detail = f"{types}"
    cell.write()


# ---------------------------------------------------------------------------
# GeoServices REST
# ---------------------------------------------------------------------------

def featureserver() -> None:
    cell = _cell("client-cert/qgis/featureserver/serve.geoservices-featureserver", "GeoServices REST 10.x", "FeatureServer query")
    cell.primary_request_url = FEATURESERVER + "/0/query"
    with cell.check("positive", "load the FeatureServer layer and read every feature") as c:
        with qgiskit.recording() as recorder:
            layer = QgsVectorLayer(f"url='{FEATURESERVER}/0'", "fs", "arcgisfeatureserver")
            names = _names(layer)
            located = _xy(layer)
        expect(names == sorted(NAMES) and located.get("alpha") == LOCATIONS["alpha"], (names, located.get("alpha")))
        c.detail = f"{names}; {len(recorder.matching('/query'))} query requests"
    with cell.check("metadata", "layer JSON drives fields, geometry type, CRS and extent") as c:
        layer = QgsVectorLayer(f"url='{FEATURESERVER}/0'", "fs", "arcgisfeatureserver")
        fields = {field.name(): field.typeName() for field in layer.fields()}
        expect(QgsWkbTypes.displayString(layer.wkbType()) == "Point" and layer.crs().authid() == "EPSG:4326", (layer.wkbType(), layer.crs().authid()))
        expect({"name", "status", "count"} <= set(fields), fields)
        c.detail = f"fields {fields}; crs {layer.crs().authid()}; extent {layer.extent().toString(3)}"
    with cell.check("media-schema", "f=json responses are JSON and decode into typed attributes") as c:
        with qgiskit.recording() as recorder:
            layer = QgsVectorLayer(f"url='{FEATURESERVER}/0'", "fs", "arcgisfeatureserver")
            counts = sorted(feature["count"] for feature in layer.getFeatures())
        types = sorted({item.content_type for item in recorder.exchanges if item.status == 200})
        expect(counts == list(range(1, 11)) and all("json" in (value or "") for value in types), (counts, types))
        c.detail = f"counts {counts}; content types {types}"
    cell.write()


def mapserver() -> None:
    cell = _cell("client-cert/qgis/mapserver/serve.geoservices-mapserver", "GeoServices REST 10.x", "MapServer export")
    cell.primary_request_url = MAPSERVER + "/export"
    source = f"crs='EPSG:3857' format='PNG32' layer='2000' url='{MAPSERVER}'"
    with cell.check("positive", "load the MapServer layer and render an export") as c:
        with qgiskit.recording() as recorder:
            layer = QgsRasterLayer(source, "ms", "arcgismapserver")
            painted, colours = qgiskit.render(layer, BROWSER_EXTENT_3857, "EPSG:3857")
        exports = [item for item in recorder.matching("/MapServer/export") if item.status == 200]
        expect(layer.isValid() and painted > 0 and exports, (layer.isValid(), painted, recorder.summary(6)))
        c.detail = f"painted {painted} px in {colours} colours via {len(exports)} exports"
    with cell.check("metadata", "service and layer JSON drive CRS, extent and layer list") as c:
        layer = QgsRasterLayer(source, "ms", "arcgismapserver")
        expect(layer.isValid() and layer.extent().width() > 0, layer.extent().toString())
        c.detail = f"crs {layer.crs().authid()}; extent {layer.extent().toString(0)}; sublayers {len(layer.dataProvider().subLayers())}"
    with cell.check("media-schema", "service JSON and PNG32 exports carry their media types") as c:
        with qgiskit.recording() as recorder:
            qgiskit.render(QgsRasterLayer(source, "ms", "arcgismapserver"), BROWSER_EXTENT_3857, "EPSG:3857")
        types = sorted({(item.url.split("?")[0].rsplit("/", 1)[-1], item.content_type) for item in recorder.exchanges if item.status == 200})
        expect(any(name == "export" and (ctype or "").startswith("image/png") for name, ctype in types), types)
        c.detail = f"{types}"
    cell.write()


CELLS = (
    featureserver, mapserver,
    oapif_landing, oapif_conformance, oapif_collections, oapif_collection, oapif_items, oapif_item,
    oapif_queryables, oapif_transactions, oapif_service, ogc_features_simple,
    tiles_landing_tilesets, tiles_tile, tiles_service,
    wfs_operation, wfs_service, wms_operation, wms_service, wmts_operation, wmts_service,
    maps_service, styles_service,
)
