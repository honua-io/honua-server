"""MapLibre GL JS bounded-roster cells (lane ``js-maplibre``).

Governed releases: "5.7" (the pinned 5.7.x build, recorded exactly in the lane
identity) for the OGC surfaces and "6.5.0" for Terrain-RGB.
"""
from __future__ import annotations

import os
import re
from xml.etree import ElementTree

import browserkit
from cellkit import Cell, expect
from rosterenv import BASE_URL, EXPIRED_BEARER, WRONG_API_KEY, api_key_headers, bearer_headers

ALPHA = [-122.4194, 37.7749]          # browser_compat point pt-alpha
TEST_ALPHA = [-122.49, 37.71]         # test_service point "alpha" (mirrored by protected collection 2011)
EMPTY_SEA = [-123.5, 37.0]            # nothing is published here
CENTER, ZOOM = [-122.4194, 37.7749], 14
DENIED = {"anonymous": None, "wrong-api-key": WRONG_API_KEY, "expired-bearer": EXPIRED_BEARER}
ADMITTED = {"api-key": api_key_headers(), "oidc-bearer": bearer_headers()}
BBOX_3857 = "{bbox-epsg-3857}"

WMS_BASE = "/rest/services/{service}/MapServer/WMS"
WMTS_BASE = "/rest/services/{service}/MapServer/WMTS"


def _cell(test_id: str, version: str, profile: str, release: str) -> Cell:
    return Cell(test_id, client_version_detail=f"maplibre-gl@{_release_version(release)}",
                protocol_version=version, protocol_profile=profile)


def _release_version(release: str) -> str:
    versions = os.environ.get("ROSTER_MAPLIBRE_VERSIONS_FILE", "/opt/roster-assets/versions.txt")
    try:
        for line in open(versions, encoding="utf-8"):
            name, _, version = line.strip().partition(" ")
            if name == release and re.fullmatch(r"\d+\.\d+\.\d+", version):
                return version
    except OSError:
        pass
    return release


def _raster_style(tiles: str, size: int = 256) -> dict:
    return {"version": 8, "sources": {"r": {"type": "raster", "tiles": [BASE_URL + tiles], "tileSize": size}},
            "layers": [{"id": "r", "type": "raster", "source": "r"}]}


def _vector_style(tiles: str) -> dict:
    return {"version": 8, "sources": {"v": {"type": "vector", "tiles": [BASE_URL + tiles], "minzoom": 0, "maxzoom": 16}},
            "layers": [{"id": "points", "type": "circle", "source": "v", "source-layer": "layer",
                        "paint": {"circle-radius": 6, "circle-color": "#d00000"}}]}


def _probes(query: bool = False, pixel: bool = False, at: list[float] = ALPHA) -> list[dict]:
    return [{"name": "alpha", "lngLat": at, "query": query, "pixel": pixel},
            {"name": "empty", "lngLat": EMPTY_SEA, "query": query, "pixel": pixel}]


def _statuses(outcome: dict, fragment: str) -> list[int]:
    return sorted({response["status"] for response in outcome["responses"] if fragment in response["url"]})


def _types(outcome: dict, fragment: str) -> list[str]:
    return sorted({(response["content_type"] or "").split(";")[0] for response in outcome["responses"]
                   if fragment in response["url"] and response["status"] == 200})


# ---------------------------------------------------------------------------
# shared facet checks
# ---------------------------------------------------------------------------

def _auth_check(cell: Cell, session, style_for, fragment: str, raster: bool, at: list[float] = ALPHA) -> None:
    with cell.check("auth", "transformRequest credentials gate the protected source") as c:
        outcomes = {}
        for label, headers in {**DENIED, **ADMITTED}.items():
            outcome = session.render(style_for(), center=at, zoom=ZOOM, headers=headers,
                                     probes=_probes(query=not raster, pixel=raster, at=at))
            alpha = outcome["probes"]["alpha"]
            drawn = alpha.get("painted", 0) if raster else len(alpha.get("features", []))
            served = sorted({(response["status"], (response["content_type"] or "").split(";")[0])
                             for response in outcome["responses"] if fragment in response["url"]})
            outcomes[label] = (drawn, served)
        for label in DENIED:
            # Refused either with 401/403, or -- on the WMS KVP alias, for a token the
            # shared formatter rejects -- with an OGC XML exception instead of an image
            # (documented PA-069/PA-074 behaviour). Never with map content.
            refused = [pair for pair in outcomes[label][1]
                       if pair[0] in (401, 403) or (pair[0] == 200 and "xml" in pair[1])]
            expect(outcomes[label][0] == 0 and refused and all(
                pair in refused for pair in outcomes[label][1] if pair[0] == 200), f"{label}: {outcomes[label]}")
        for label in ADMITTED:
            expect(outcomes[label][0] > 0 and any(pair[0] == 200 for pair in outcomes[label][1]), f"{label}: {outcomes[label]}")
        c.detail = f"(drawn at pt-alpha, statuses) {outcomes}"


def _negative_check(cell: Cell, session, style, fragment: str, name: str) -> None:
    with cell.check("negative", name) as c:
        raster = style["layers"][0]["type"] == "raster"
        outcome = session.render(style, center=CENTER, zoom=ZOOM, probes=_probes(query=not raster, pixel=raster))
        statuses = _statuses(outcome, fragment)
        alpha = outcome["probes"]["alpha"]
        drawn = alpha.get("painted", 0) if raster else len(alpha.get("features", []))
        error_statuses = sorted({error.get("status") for error in outcome["errors"] if error.get("status")})
        # MapLibre drops failed tiles without necessarily raising a map error event;
        # the governed behaviour is a client-error response and nothing rendered.
        expect(statuses and all(400 <= status < 500 for status in statuses) and drawn == 0,
               f"responses {statuses}; drawn {drawn}; MapLibre error events {outcome['errors'][:3]}")
        c.detail = f"responses {statuses}; nothing rendered; MapLibre error statuses {error_statuses}"


def _crs_raster_check(cell: Cell, session, style) -> None:
    with cell.check("crs-axis", "EPSG:3857 content lands on pt-alpha's projected pixel and nowhere empty") as c:
        outcome = session.render(style, center=CENTER, zoom=ZOOM, probes=_probes(pixel=True))
        alpha, empty = outcome["probes"]["alpha"]["painted"], outcome["probes"]["empty"]
        far = session.render(style, center=EMPTY_SEA, zoom=ZOOM, probes=[{"name": "sea", "lngLat": EMPTY_SEA, "pixel": True}])
        expect(alpha > 0 and far["probes"]["sea"]["painted"] == 0, (alpha, far["probes"]["sea"]))
        c.detail = f"painted pixels around pt-alpha {alpha}; around empty sea {far['probes']['sea']['painted']}"


def _crs_vector_check(cell: Cell, session, style) -> None:
    with cell.check("crs-axis", "WebMercatorQuad tile features project back onto pt-alpha") as c:
        outcome = session.render(style, center=CENTER, zoom=ZOOM, probes=_probes(query=True))
        names = [feature["properties"].get("name") for feature in outcome["probes"]["alpha"]["features"]]
        far = session.render(style, center=EMPTY_SEA, zoom=ZOOM, probes=[{"name": "sea", "lngLat": EMPTY_SEA, "query": True}])
        expect("pt-alpha" in names and not far["probes"]["sea"]["features"], (names, far["probes"]["sea"]))
        c.detail = f"features at pt-alpha's projected pixel {names}; at empty sea {far['probes']['sea']['features']}"


def _media_check(cell: Cell, session, style, fragment: str, expected: str) -> None:
    with cell.check("media-schema", f"responses decode as {expected}") as c:
        outcome = session.render(style, center=CENTER, zoom=ZOOM)
        types = _types(outcome, fragment)
        expect(types and all(value == expected for value in types) and not outcome["errors"],
               f"types {types}; errors {outcome['errors'][:3]}")
        c.detail = f"{types}; no MapLibre errors; {browserkit.summarize(outcome)}"


# ---------------------------------------------------------------------------
# WMS / WMTS
# ---------------------------------------------------------------------------

def _wms_tiles(service: str = "browser_compat", layers: str = "Browser Points") -> str:
    return (WMS_BASE.format(service=service) + "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap"
            f"&LAYERS={layers.replace(' ', '%20')}&STYLES=&CRS=EPSG:3857&BBOX={BBOX_3857}"
            "&WIDTH=256&HEIGHT=256&FORMAT=image/png&TRANSPARENT=TRUE")


def _wms_checks(cell: Cell, session, feature_info: bool) -> None:
    with cell.check("positive", "GetMap through a raster source" + ("; GetCapabilities and GetFeatureInfo from the map application" if feature_info else "")) as c:
        detail = []
        if feature_info:
            capabilities = session.fetch(BASE_URL + WMS_BASE.format(service="browser_compat") + "?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0")
            root = ElementTree.fromstring(capabilities["body"])
            names = [element.text for element in root.iter() if element.tag.endswith("}Name") and element.text]
            expect(capabilities["status"] == 200 and "Browser Points" in names, (capabilities["status"], names[:6]))
            detail.append(f"capabilities layers {names[1:5]}")
        outcome = session.render(_raster_style(_wms_tiles()), center=CENTER, zoom=ZOOM, probes=_probes(pixel=True))
        alpha = outcome["probes"]["alpha"]
        expect(alpha["painted"] > 0 and 200 in _statuses(outcome, "REQUEST=GetMap"), browserkit.summarize(outcome))
        detail.append(f"painted {alpha['painted']} px around pt-alpha")
        if feature_info:
            x, y = alpha["point"]
            # CRS:84 is lon/lat in WMS 1.3.0; pt-alpha sits at the centre pixel of this window.
            info = session.fetch(
                BASE_URL + WMS_BASE.format(service="browser_compat") + "?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetFeatureInfo"
                "&LAYERS=Browser%20Points&QUERY_LAYERS=Browser%20Points&STYLES=&CRS=CRS:84"
                "&BBOX=-122.4204,37.7739,-122.4184,37.7759&WIDTH=256&HEIGHT=256&INFO_FORMAT=application/json&I=128&J=128")
            expect(info["status"] == 200 and "pt-alpha" in info["body"], (info["status"], info["body"][:200]))
            detail.append("GetFeatureInfo at pt-alpha returned pt-alpha")
        c.detail = "; ".join(detail)
    _negative_check(cell, session, _raster_style(_wms_tiles(layers="No Such Layer")), "REQUEST=GetMap",
                    "an unknown WMS layer surfaces as a MapLibre tile error")
    _auth_check(cell, session, lambda: _raster_style(_wms_tiles("cert_auth_raster", "Authenticated Browser Points")),
                "REQUEST=GetMap", raster=True)
    _crs_raster_check(cell, session, _raster_style(_wms_tiles()))
    _media_check(cell, session, _raster_style(_wms_tiles()), "REQUEST=GetMap", "image/png")


def _wmts_template(session, service: str = "browser_compat", layer: str = "2000", headers: dict | None = None) -> str:
    capabilities = session.fetch(BASE_URL + WMTS_BASE.format(service=service) + "?SERVICE=WMTS&REQUEST=GetCapabilities", headers)
    expect(capabilities["status"] == 200, f"GetCapabilities {capabilities['status']}")
    templates = re.findall(r'resourceType="tile" template="([^"]+)"', capabilities["body"])
    template = next((value for value in templates if f"/WMTS/{layer}/" in value), None)
    expect(template, f"no tile ResourceURL for layer {layer}: {templates[:3]}")
    return (template.replace(BASE_URL, "").replace("{Style}", "default").replace("{TileMatrixSet}", "WebMercatorQuad")
            .replace("{TileMatrix}", "{z}").replace("{TileRow}", "{y}").replace("{TileCol}", "{x}"))


def _wmts_checks(cell: Cell, session) -> None:
    template = None
    with cell.check("positive", "tile template from GetCapabilities rendered through a raster source") as c:
        template = _wmts_template(session)
        outcome = session.render(_raster_style(template), center=CENTER, zoom=ZOOM, probes=_probes(pixel=True))
        expect(outcome["probes"]["alpha"]["painted"] > 0 and 200 in _statuses(outcome, "/WMTS/2000/"), browserkit.summarize(outcome))
        c.detail = f"template {template}; painted {outcome['probes']['alpha']['painted']} px around pt-alpha"
    template = template or "/rest/services/browser_compat/MapServer/WMTS/2000/default/WebMercatorQuad/{z}/{y}/{x}.png"
    _negative_check(cell, session, _raster_style(template.replace("/WMTS/2000/", "/WMTS/999999/")), "/WMTS/999999/",
                    "an unknown WMTS layer surfaces as a MapLibre tile error")
    protected = template.replace("browser_compat", "cert_auth_raster").replace("/WMTS/2000/", "/WMTS/2010/")
    _auth_check(cell, session, lambda: _raster_style(protected), "/WMTS/2010/", raster=True)
    _crs_raster_check(cell, session, _raster_style(template))
    _media_check(cell, session, _raster_style(template), "/WMTS/2000/", "image/png")


def wms_operation() -> None:
    cell = _cell("client-cert/maplibre-gl-js/ogc/OGC-OP-WMS-GETCAPABILITIES-GETMAP-GETFEATUREINF", "1.3.0", "WMS 1.3.0 queryable", "maplibre-5.7")
    cell.primary_request_url = BASE_URL + WMS_BASE.format(service="browser_compat") + "?SERVICE=WMS&REQUEST=GetMap"
    with browserkit.browser("maplibre-5.7", BASE_URL) as session:
        _wms_checks(cell, session, feature_info=True)
    cell.write()


def wms_service() -> None:
    cell = _cell("client-cert/maplibre-gl-js/wms/serve.wms", "1.3.0", "WMS 1.3.0", "maplibre-5.7")
    cell.primary_request_url = BASE_URL + WMS_BASE.format(service="browser_compat") + "?SERVICE=WMS&REQUEST=GetMap"
    with browserkit.browser("maplibre-5.7", BASE_URL) as session:
        _wms_checks(cell, session, feature_info=False)
    cell.write()


def wmts_operation() -> None:
    cell = _cell("client-cert/maplibre-gl-js/ogc/OGC-OP-WMTS-GETCAPABILITIES-GETTILE", "1.0.0", "WMTS 1.0.0 RESTful", "maplibre-5.7")
    cell.primary_request_url = BASE_URL + WMTS_BASE.format(service="browser_compat") + "/2000/default/WebMercatorQuad/14/6331/2621.png"
    with browserkit.browser("maplibre-5.7", BASE_URL) as session:
        _wmts_checks(cell, session)
    cell.write()


def wmts_service() -> None:
    cell = _cell("client-cert/maplibre-gl-js/wmts/serve.wmts", "1.0.0", "WMTS 1.0.0 RESTful", "maplibre-5.7")
    cell.primary_request_url = BASE_URL + WMTS_BASE.format(service="browser_compat") + "/2000/default/WebMercatorQuad/14/6331/2621.png"
    with browserkit.browser("maplibre-5.7", BASE_URL) as session:
        _wmts_checks(cell, session)
    cell.write()


# ---------------------------------------------------------------------------
# OGC API - Tiles / Maps / Styles
# ---------------------------------------------------------------------------

def _tile_template(collection: str = "2000") -> str:
    return f"/ogc/tiles/collections/{collection}/tiles/WebMercatorQuad/{{z}}/{{y}}/{{x}}?f=mvt"


def _tiles_checks(cell: Cell, session, positive_name: str, positive) -> None:
    with cell.check("positive", positive_name) as c:
        c.detail = positive()
    _negative_check(cell, session, _vector_style(_tile_template("no-such-collection")), "/collections/no-such-collection/",
                    "an unknown collection's tiles surface as a MapLibre error")
    _auth_check(cell, session, lambda: _vector_style(_tile_template("2011")), "/collections/2011/", raster=False,
                at=TEST_ALPHA)
    _crs_vector_check(cell, session, _vector_style(_tile_template()))
    _media_check(cell, session, _vector_style(_tile_template()), "/tiles/WebMercatorQuad/", "application/vnd.mapbox-vector-tile")


def _query_positive(session, template: str) -> str:
    outcome = session.render(_vector_style(template), center=CENTER, zoom=ZOOM, probes=_probes(query=True))
    names = [feature["properties"].get("name") for feature in outcome["probes"]["alpha"]["features"]]
    expect("pt-alpha" in names, f"{names}; {browserkit.summarize(outcome)}")
    return f"queryRenderedFeatures at pt-alpha -> {names}; {browserkit.summarize(outcome)}"


def tiles_tile() -> None:
    cell = _cell("client-cert/maplibre-gl-js/ogc/OGC-OP-OGC-API-TILES-TILE", "OGC API - Tiles 1.0", "core+mvt", "maplibre-5.7")
    cell.primary_request_url = BASE_URL + "/ogc/tiles/collections/2000/tiles/WebMercatorQuad/14/6331/2621?f=mvt"
    with browserkit.browser("maplibre-5.7", BASE_URL) as session:
        _tiles_checks(cell, session, "render and query vector tiles", lambda: _query_positive(session, _tile_template()))
    cell.write()


def tiles_landing_tilesets() -> None:
    cell = _cell("client-cert/maplibre-gl-js/ogc/OGC-OP-OGC-API-TILES-LANDING-TILESETS", "OGC API - Tiles 1.0", "core+tilesets-list", "maplibre-5.7")
    cell.primary_request_url = BASE_URL + "/ogc/tiles/collections/2000/tiles"

    def positive() -> str:
        import json
        landing = session.fetch(BASE_URL + "/ogc/tiles")
        tilesets = session.fetch(BASE_URL + "/ogc/tiles/collections/2000/tiles")
        expect(landing["status"] == 200 and tilesets["status"] == 200, (landing["status"], tilesets["status"]))
        listed = json.loads(tilesets["body"])["tilesets"]
        vector = next(tileset for tileset in listed if tileset.get("tileMatrixSetId") == "WebMercatorQuad")
        detail_url = next((link["href"] for link in vector.get("links", []) if link.get("rel") == "self"),
                          BASE_URL + "/ogc/tiles/collections/2000/tiles/WebMercatorQuad")
        detail = json.loads(session.fetch(detail_url)["body"])
        item = next(link["href"] for link in detail["links"] if link.get("rel") == "item")
        template = (item.replace(BASE_URL, "").replace("{tileMatrix}", "{z}").replace("{tileRow}", "{y}")
                    .replace("{tileCol}", "{x}"))
        template += ("&" if "?" in template else "?") + "f=mvt"
        return f"tileset item template {item} -> {_query_positive(session, template)}"

    with browserkit.browser("maplibre-5.7", BASE_URL) as session:
        _tiles_checks(cell, session, "discover the tileset from the landing/tilesets documents and render its item template", positive)
    cell.write()


def tiles_service() -> None:
    cell = _cell("client-cert/maplibre-gl-js/ogc-api-tiles/serve.ogc-api-tiles", "OGC API - Tiles 1.0", "core+mvt", "maplibre-5.7")
    cell.primary_request_url = BASE_URL + "/ogc/tiles/collections/2000/tiles/WebMercatorQuad/14/6331/2621?f=mvt"
    with browserkit.browser("maplibre-5.7", BASE_URL) as session:
        _tiles_checks(cell, session, "render and query vector tiles", lambda: _query_positive(session, _tile_template()))
    cell.write()


def _map_tiles(collection: str = "2000") -> str:
    return (f"/ogc/maps/collections/{collection}/map?bbox={BBOX_3857}"
            "&bbox-crs=http://www.opengis.net/def/crs/EPSG/0/3857&crs=http://www.opengis.net/def/crs/EPSG/0/3857"
            "&width=256&height=256&transparent=true&f=png")


def maps_service() -> None:
    cell = _cell("client-cert/maplibre-gl-js/ogc-api-maps/serve.ogc-api-maps", "OGC API - Maps 1.0", "core+collection-map+crs", "maplibre-5.7")
    cell.primary_request_url = BASE_URL + "/ogc/maps/collections/2000/map"
    with browserkit.browser("maplibre-5.7", BASE_URL) as session:
        with cell.check("positive", "EPSG:3857 bbox maps tiled through a raster source") as c:
            outcome = session.render(_raster_style(_map_tiles()), center=CENTER, zoom=ZOOM, probes=_probes(pixel=True))
            expect(outcome["probes"]["alpha"]["painted"] > 0 and 200 in _statuses(outcome, "/map?"), browserkit.summarize(outcome))
            c.detail = f"painted {outcome['probes']['alpha']['painted']} px around pt-alpha; {browserkit.summarize(outcome)}"
        _negative_check(cell, session, _raster_style(_map_tiles("no-such-collection")), "/collections/no-such-collection/",
                        "an unknown collection map surfaces as a MapLibre error")
        with cell.check("auth", "transformRequest credentials gate the protected collection map") as c:
            outcomes = {}
            for label, headers in {**DENIED, **ADMITTED}.items():
                outcome = session.render(_raster_style(_map_tiles("2010")), center=CENTER, zoom=ZOOM, headers=headers,
                                         probes=_probes(pixel=True))
                outcomes[label] = (outcome["probes"]["alpha"]["painted"], _statuses(outcome, "/collections/2010/"))
            expect(all(outcomes[label][0] == 0 and outcomes[label][1] and 200 not in outcomes[label][1] for label in DENIED), outcomes)
            expect(all(outcomes[label][0] > 0 and 200 in outcomes[label][1] for label in ADMITTED), outcomes)
            c.detail = f"(painted, statuses) {outcomes}"
        _crs_raster_check(cell, session, _raster_style(_map_tiles()))
        _media_check(cell, session, _raster_style(_map_tiles()), "/map?", "image/png")
    cell.write()


def styles_service() -> None:
    cell = _cell("client-cert/maplibre-gl-js/ogc-api-styles/styling.ogc-api-styles", "OGC API - Styles 1.0", "core+mapbox-styles", "maplibre-5.7")
    cell.primary_request_url = BASE_URL + "/ogc/styles"
    with browserkit.browser("maplibre-5.7", BASE_URL) as session:
        import json

        def advertised_stylesheet(collection: str = "2000", headers: dict | None = None) -> str:
            document = json.loads(session.fetch(BASE_URL + f"/ogc/features/collections/{collection}", headers)["body"])
            return next(link["href"] for link in document["links"] if link.get("rel") == "stylesheet")

        with cell.check("positive", "load the collection's advertised stylesheet as the map style") as c:
            listed = json.loads(session.fetch(BASE_URL + "/ogc/styles")["body"])
            stylesheet = advertised_stylesheet()
            outcome = session.page.evaluate("""async (url) => {
                const errors = [];
                const map = new maplibregl.Map({container: 'map', style: url, center: [-122.4194, 37.7749], zoom: 14});
                map.on('error', (e) => errors.push({status: e.error && e.error.status, message: String(e.error && e.error.message)}));
                const loaded = await Promise.race([new Promise((r) => map.once('style.load', () => r(true))),
                                                   new Promise((r) => setTimeout(() => r(false), 15000))]);
                const layers = loaded ? map.getStyle().layers.map((layer) => layer.id) : [];
                map.remove();
                return {loaded, errors, layers};
            }""", stylesheet + "?f=mapbox")
            expect(outcome["loaded"] and outcome["layers"],
                   f"/ogc/styles lists {len(listed.get('styles', []))} styles; the collection advertises {stylesheet} "
                   f"(rel=stylesheet) but MapLibre could not load it: {outcome['errors'][:2]}")
            c.detail = f"style layers {outcome['layers']}"
        with cell.check("negative", "an unknown style id surfaces as a MapLibre style error") as c:
            outcome = session.page.evaluate("""async (url) => {
                const errors = [];
                const map = new maplibregl.Map({container: 'map', style: url});
                map.on('error', (e) => errors.push(e.error && e.error.status));
                await new Promise((r) => setTimeout(r, 4000));
                map.remove();
                return errors;
            }""", BASE_URL + "/ogc/styles/no-such-style?f=mapbox")
            expect(404 in outcome, outcome)
            c.detail = f"style error statuses {outcome}"
        with cell.check("auth", "the protected collection's stylesheet requires a credential") as c:
            anonymous = session.fetch(BASE_URL + "/ogc/features/collections/2010")
            expect(anonymous["status"] in (401, 403, 404), f"anonymous collection {anonymous['status']}")
            stylesheet = advertised_stylesheet("2010", api_key_headers())
            statuses = {label: session.fetch(stylesheet + "?f=mapbox", headers)["status"]
                        for label, headers in {**DENIED, **ADMITTED}.items()}
            expect(all(statuses[label] in (401, 403) for label in DENIED) and all(statuses[label] == 200 for label in ADMITTED), statuses)
            c.detail = f"{stylesheet}: {statuses}"
        with cell.check("media-schema", "stylesheets are served as application/vnd.mapbox.style+json") as c:
            response = session.fetch(advertised_stylesheet() + "?f=mapbox")
            expect(response["status"] == 200 and "mapbox.style+json" in (response["contentType"] or ""),
                   (response["status"], response["contentType"]))
            c.detail = f"{response['contentType']}"
    cell.write()


# ---------------------------------------------------------------------------
# Terrain-RGB (MapLibre 6.5.0)
# ---------------------------------------------------------------------------

DEM_ORIGIN = (-122.46, 37.80)
DEM_CELL = 0.000625


def expected_elevation(lng: float, lat: float) -> float:
    """roster-raster-fixture.sql: elevation(col, row) = 100 + col + 2 * row, row 0 at the north edge."""
    col = int((lng - DEM_ORIGIN[0]) / DEM_CELL)
    row = int((DEM_ORIGIN[1] - lat) / DEM_CELL)
    return 100 + col + 2 * row


def _terrain_style(dataset: str = "5100") -> dict:
    return {"version": 8,
            "sources": {"dem": {"type": "raster-dem", "url": f"{BASE_URL}/terrain/{dataset}/tile.json",
                                "encoding": "mapbox", "tileSize": 256}},
            "layers": [{"id": "hillshade", "type": "hillshade", "source": "dem"}]}


def terrain() -> None:
    cell = _cell("client-cert/maplibre-gl-js/raster-terrain-rgb/raster.terrain-rgb", "TileJSON 3.0 + Terrain-RGB",
                 "raster-dem mapbox encoding", "maplibre-6.5.0")
    cell.primary_request_url = BASE_URL + "/terrain/5100/tile.json"
    inside = {"name": "inside", "lngLat": [-122.42, 37.76], "elevation": True}
    north = {"name": "north", "lngLat": [-122.42, 37.79], "elevation": True}
    east = {"name": "east", "lngLat": [-122.39, 37.76], "elevation": True}
    outside = {"name": "outside", "lngLat": [-122.50, 37.76], "elevation": True}
    view = dict(center=[-122.42, 37.76], zoom=13)
    with browserkit.browser("maplibre-6.5.0", BASE_URL) as session:
        with cell.check("positive", "setTerrain on the raster-dem source and read the DEM back with queryTerrainElevation") as c:
            outcome = session.render(_terrain_style(), **view, probes=[inside], terrain={"source": "dem", "exaggeration": 1})
            measured = outcome["probes"]["inside"]["elevation"]
            expected = expected_elevation(*inside["lngLat"])
            expect(outcome["version"] == "6.5.0", outcome["version"])
            expect(measured is not None and abs(measured - expected) <= 4, f"elevation {measured} vs {expected}; {browserkit.summarize(outcome)}")
            c.detail = f"MapLibre {outcome['version']}: elevation at {inside['lngLat']} = {measured:.1f} m (fixture {expected} m)"
        with cell.check("negative", "an unknown terrain dataset is refused") as c:
            outcome = session.render(_terrain_style("999999"), **view, probes=[inside], terrain={"source": "dem", "exaggeration": 1})
            statuses = _statuses(outcome, "/terrain/999999/")
            expect(statuses and all(status == 404 for status in statuses) and outcome["errors"], (statuses, outcome["errors"][:2]))
            c.detail = f"responses {statuses}; MapLibre errors {[error.get('status') for error in outcome['errors']][:3]}"
        with cell.check("auth", "the protected DEM's TileJSON and tiles require transformRequest credentials") as c:
            outcomes = {}
            for label, headers in {**DENIED, **ADMITTED}.items():
                outcome = session.render(_terrain_style("5101"), **view, headers=headers, probes=[inside],
                                         terrain={"source": "dem", "exaggeration": 1})
                outcomes[label] = (outcome["probes"]["inside"]["elevation"], _statuses(outcome, "/terrain/5101/"))
            expected = expected_elevation(*inside["lngLat"])
            expect(all(outcomes[label][0] in (None, 0) and outcomes[label][1] and set(outcomes[label][1]) <= {401, 403}
                       for label in DENIED), outcomes)
            cache = session.page.evaluate("""async (headers) => {
                const read = async (h) => (await fetch('/terrain/5101/14/2621/6333.png', {headers: h})).headers.get('cache-control');
                const anonymous = (await fetch('/terrain/5100/14/2621/6333.png')).headers.get('cache-control');
                return {authenticated: await read(headers), anonymous_public: anonymous};
            }""", api_key_headers())
            expect(all(outcomes[label][0] is not None and abs(outcomes[label][0] - expected) <= 4 for label in ADMITTED),
                   f"{outcomes}: credentialed tiles are fetched (200) but MapLibre never uses them for terrain; "
                   f"Cache-Control on those responses {cache}")
            c.detail = f"(elevation, statuses) {outcomes}"
        with cell.check("boundary", "tiles beyond the DEM extent are served as the -10000 m no-data sentinel") as c:
            outcome = session.render(_terrain_style(), center=[-122.46, 37.76], zoom=13, probes=[outside, inside],
                                     terrain={"source": "dem", "exaggeration": 1})
            measured = outcome["probes"]["outside"]["elevation"]
            png = [response for response in outcome["responses"] if "/terrain/5100/" in response["url"] and response["url"].endswith(".png")]
            expect(measured is not None and measured <= -9990, f"elevation outside the DEM {measured}")
            expect(png and all(response["status"] == 200 for response in png), [(r["status"], r["url"]) for r in png][:4])
            c.detail = f"elevation west of the DEM {measured}; {len(png)} tiles all 200"
        with cell.check("crs-axis", "the gradient runs east (+1 m/cell) and south (+2 m/cell) as the fixture encodes it") as c:
            outcome = session.render(_terrain_style(), **view, probes=[inside, north, east], terrain={"source": "dem", "exaggeration": 1})
            values = {name: outcome["probes"][name]["elevation"] for name in ("inside", "north", "east")}
            expected = {name: expected_elevation(*probe["lngLat"]) for name, probe in (("inside", inside), ("north", north), ("east", east))}
            expect(all(values[name] is not None and abs(values[name] - expected[name]) <= 4 for name in values), (values, expected))
            expect(values["north"] < values["inside"] and values["east"] > values["inside"], values)
            c.detail = f"measured {values}; fixture {expected}"
        with cell.check("media-schema", "TileJSON declares terrain-rgb/mapbox and tiles are PNG") as c:
            import json
            metadata = session.fetch(BASE_URL + "/terrain/5100/tile.json")
            document = json.loads(metadata["body"])
            outcome = session.render(_terrain_style(), **view, terrain={"source": "dem", "exaggeration": 1})
            types = _types(outcome, "/terrain/5100/")
            expect(metadata["status"] == 200 and (metadata["contentType"] or "").startswith("application/json"), metadata["contentType"])
            expect(document.get("format") == "terrain-rgb" and document.get("tilejson", "").startswith("3."), {k: document.get(k) for k in ("format", "tilejson", "encoding")})
            expect("image/png" in types, types)
            c.detail = f"TileJSON {document.get('tilejson')} format {document.get('format')} encoding {document.get('encoding')}; response types {types}"
    cell.write()


CELLS = (wms_operation, wms_service, wmts_operation, wmts_service, tiles_tile, tiles_landing_tilesets, tiles_service,
         maps_service, styles_service, terrain)
