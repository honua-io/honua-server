"""PySTAC-Client 0.9.0 / PySTAC 1.15.2 bounded-roster cell (lane ``py-pystac``)."""
from __future__ import annotations

import pystac
import pystac_client
from pystac_client import Client
from pystac_client.exceptions import APIError
from pystac_client.stac_api_io import StacApiIO

from cellkit import Cell, expect
from rosterenv import EXPIRED_BEARER, WRONG_API_KEY, api_key_headers, bearer_headers, url

CLIENT_DETAIL = f"pystac=={pystac.__version__};pystac-client=={pystac_client.__version__}"

# Fixture: test_service layer 0 (10 point features, 2024-01-01..2024-01-10) is the
# public STAC collection "0"; layer 2011 is its access-controlled mirror.
PUBLIC_COLLECTION = "0"
PROTECTED_COLLECTION = "2011"
FIXTURE_ITEMS = 10


def _api_status(error: APIError) -> int | None:
    return getattr(error, "status_code", None)


def stac() -> None:
    cell = Cell("client-cert/pystac-client/stac/serve.stac", client_version_detail=CLIENT_DETAIL,
                protocol_version="STAC API 1.0.0", protocol_profile="core+collections+item-search+ogcapi-features")
    endpoint = url("/stac")
    cell.primary_request_url = endpoint + "/search"

    with cell.check("positive", "open the catalog, read a collection and search its items") as c:
        client = Client.open(endpoint)
        expect(client.conforms_to("ITEM_SEARCH") and client.conforms_to("COLLECTIONS"), "missing conformance")
        collection = client.get_collection(PUBLIC_COLLECTION)
        items = list(client.search(collections=[PUBLIC_COLLECTION], limit=100).items())
        expect(len(items) == FIXTURE_ITEMS, f"{len(items)} items, fixture has {FIXTURE_ITEMS}")
        bbox = collection.extent.spatial.bboxes[0]
        # The fixture deliberately carries one feature with a null geometry; STAC 1.0
        # requires such an item to omit bbox, which is what is checked for it.
        null_geometry = [item.id for item in items if item.geometry is None]
        expect(all(item.bbox is None for item in items if item.geometry is None), "a null-geometry item carries a bbox")
        outside = [item.id for item in items if item.geometry is not None and not (bbox[0] <= item.bbox[0] and item.bbox[2] <= bbox[2]
                                                     and bbox[1] <= item.bbox[1] and item.bbox[3] <= bbox[3])]
        expect(not outside, f"items outside the collection extent: {outside}")
        c.detail = (f"catalog {client.id}; collection {collection.id} extent {bbox}; "
                    f"search returned {len(items)} items; located items inside the extent; null-geometry items {null_geometry}")

    with cell.check("pagination", "item search follows next links to every item exactly once") as c:
        client = Client.open(endpoint)
        search = client.search(collections=[PUBLIC_COLLECTION], limit=3)
        pages = list(search.pages_as_dicts())
        ids = [feature["id"] for page in pages for feature in page["features"]]
        expect(len(pages) == 4 and [len(page["features"]) for page in pages] == [3, 3, 3, 1],
               f"page sizes {[len(page['features']) for page in pages]}")
        expect(len(ids) == len(set(ids)) == FIXTURE_ITEMS, f"{len(ids)} ids, {len(set(ids))} unique")
        expect(search.matched() == FIXTURE_ITEMS, f"matched {search.matched()}")
        c.detail = f"{len(pages)} pages of 3 -> {len(ids)} unique items; matched() {search.matched()}"

    with cell.check("limit", "limit bounds each page and max_items bounds the result") as c:
        client = Client.open(endpoint)
        first = next(client.search(collections=[PUBLIC_COLLECTION], limit=4).pages_as_dicts())
        capped = list(client.search(collections=[PUBLIC_COLLECTION], limit=4, max_items=6).items())
        expect(len(first["features"]) == 4, f"limit=4 page has {len(first['features'])}")
        expect(len(capped) == 6, f"max_items=6 returned {len(capped)}")
        try:
            client.search(collections=[PUBLIC_COLLECTION], limit=0).item_collection()
            refused = "accepted"
        except Exception as error:  # noqa: BLE001 - the client itself rejects out-of-range limits
            refused = type(error).__name__
        expect(refused != "accepted", "limit=0 was accepted")
        c.detail = f"limit=4 -> page of 4; max_items=6 -> 6 items; limit=0 -> {refused}"

    with cell.check("negative", "unknown collection and invalid bbox raise APIError problems") as c:
        client = Client.open(endpoint)
        outcomes = {}
        try:
            client.get_collection("no-such-collection")
            outcomes["unknown-collection"] = "found"
        except APIError as error:
            outcomes["unknown-collection"] = _api_status(error) or str(error)[:80]
        try:
            list(client.search(collections=[PUBLIC_COLLECTION], bbox=[-122.4, 95.0, -122.3, 96.0]).items())
            outcomes["latitude-out-of-range"] = "served"
        except APIError as error:
            outcomes["latitude-out-of-range"] = _api_status(error) or str(error)[:80]
        expect(outcomes["unknown-collection"] in (404, "404") or "404" in str(outcomes["unknown-collection"]), outcomes)
        expect(outcomes["latitude-out-of-range"] != "served", outcomes)
        c.detail = f"{outcomes}"

    with cell.check("auth", "protected collection is hidden and challenged without a valid credential") as c:
        visible = {}
        for label, headers in (("anonymous", None), ("wrong-api-key", WRONG_API_KEY),
                               ("expired-bearer", EXPIRED_BEARER), ("api-key", api_key_headers()),
                               ("oidc-bearer", bearer_headers())):
            try:
                client = Client.open(endpoint, headers=headers)
                listed = PROTECTED_COLLECTION in {collection.id for collection in client.get_collections()}
                try:
                    client.get_collection(PROTECTED_COLLECTION)
                    fetched = 200
                except APIError as error:
                    fetched = _api_status(error) or "APIError"
                visible[label] = (listed, fetched)
            except APIError as error:
                visible[label] = ("open-refused", _api_status(error) or "APIError")
        for label in ("anonymous", "wrong-api-key", "expired-bearer"):
            expect(visible[label][0] is not True and visible[label][1] != 200, f"{label}: {visible[label]}")
        for label in ("api-key", "oidc-bearer"):
            expect(visible[label] == (True, 200), f"{label}: {visible[label]}")
        items = list(Client.open(endpoint, headers=api_key_headers()).search(
            collections=[PROTECTED_COLLECTION], limit=100).items())
        expect(len(items) == FIXTURE_ITEMS, f"authenticated search of the mirror returned {len(items)}")
        c.detail = f"(listed, get_collection status) {visible}; authenticated mirror search {len(items)} items"

    with cell.check("media-schema", "catalog, collection and items deserialize as STAC 1.0 objects") as c:
        client = Client.open(endpoint)
        collection = client.get_collection(PUBLIC_COLLECTION)
        items = list(client.search(collections=[PUBLIC_COLLECTION], limit=5, max_items=5).items())
        # pystac migrates objects to its own STAC version on serialization, so the
        # served version is read from the wire document through the client's IO layer.
        served = StacApiIO().read_json(endpoint)
        expect(served.get("stac_version") == "1.0.0" and served.get("type") == "Catalog", served.get("stac_version"))
        expect(isinstance(collection, pystac.Collection) and collection.license, collection)
        for item in items:
            payload = item.to_dict()
            expect(payload["type"] == "Feature" and item.datetime,
                   payload.get("id"))
            expect(item.assets, f"{item.id} has no assets")
            expect((item.geometry is None) == (item.bbox is None), f"{item.id} geometry/bbox presence disagree")
            pystac.Item.from_dict(payload)
        item_types = sorted({asset.media_type for item in items for asset in item.assets.values()})
        c.detail = (f"catalog stac_version 1.0.0; collection license {collection.license!r}; "
                    f"{len(items)} items round-trip through pystac.Item; asset media types {item_types}")
    cell.write()


CELLS = (stac,)
