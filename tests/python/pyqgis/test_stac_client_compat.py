# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""STAC API 1.0.0 compatibility exercised through the real QGIS STAC client.

Unlike every other lane in this package, STAC is **not** a QGIS data provider - it
is absent from ``QgsProviderRegistry.providerList()`` (verified in the container:
the list carries ``OAPIF``, ``WFS``, ``wms``, ``wcs``, ``sensorthings`` and friends
but nothing for STAC). The client is reached through core API classes instead:
``QgsStacController``, ``QgsStacParser``, ``QgsStacCatalog``, ``QgsStacCollection``,
``QgsStacItem`` and ``QgsStacAsset``. Everything below therefore drives those
classes directly rather than building a layer.

Three findings from introspecting the real 3.44.14 API drive the shape of this
module, and each one is load-bearing:

1. ``QgsStacController`` is **synchronous** for the two calls that matter here.
   ``fetchCollections(QUrl)`` and ``fetchItemCollection(QUrl)`` are documented as
   "using a blocking network request" and return their result directly, so no Qt
   event loop is needed and the WMS legend fetcher's signal/``QEventLoop`` shape is
   *not* required. Async twins exist (``fetchCollectionsAsync``,
   ``fetchItemCollectionAsync``) but are redundant for a test.

2. The landing page cannot be fetched through the controller at all.
   ``fetchStacObjectAsync`` is the only STAC-object entry point - there is no sync
   ``fetchStacObject`` - and its result is retrieved with ``takeStacObject``, which
   **is not exposed in the PyQGIS bindings** for 3.44.14 (``hasattr`` is False,
   although the docstring of ``fetchStacObjectAsync`` references it). The request
   itself completes - ``finishedStacObjectRequest`` fires with an empty error
   string - but Python can never claim the parsed object. The landing page is
   therefore read the way QGIS itself reads it internally: ``QgsBlockingNetworkRequest``
   (QGIS's own HTTP client) feeding ``QgsStacParser`` (QGIS's own STAC parser).

3. ``items()`` and ``collections()`` **crash the process** when the returned
   container is garbage-collected - ``Fatal Python error: Aborted`` or a
   segmentation fault, reproduced on a single fetch with nothing else in the test.
   The ``take*`` variants transfer ownership properly and exit cleanly. So this
   module only ever calls ``takeItems()`` / ``takeCollections()``, and it reads
   ``numberMatched`` / ``numberReturned`` / ``nextUrl`` *before* taking, because
   taking empties those counters.

Two client-side defects in QGIS 3.44.14 are documented by assertions here rather
than worked around silently:

- ``QgsStacItem.dateTime()`` is always invalid. This is not a server format
  problem: ``openLocalItem`` on a hand-written item whose ``properties.datetime``
  is the canonical ``2024-01-01T12:00:00Z`` still yields an invalid ``QDateTime``,
  and bare ``QDateTime.fromString`` parses every form the server emits. Temporal
  filtering is asserted through the server's ``datetime`` query parameter instead.
- ``QgsStacAsset.uri()`` is populated only for cloud-optimized assets (a COG href
  correctly becomes ``/vsicurl/...`` with provider ``gdal``). The seeded asset is
  ``application/geo+json``, so QGIS offers no STAC-native handle for it and the
  download goes through QGIS's own network stack using the parsed ``href``.

The fixture is the ``/stac`` surface over the client-compat seed. Its collection
list is *not* fixed: the live server also carries ids 10/11/12, which the OWSLib
lane creates per test, so this module asserts the stable seeded ids as a subset
rather than pinning a count. A repo doc claiming "9 collections" is stale - the
live landing page advertises 8 children.
"""

from __future__ import annotations

import json
import os
import tempfile
import time
import urllib.parse

import pytest

from .conftest import CertificationEvidenceCollector

STAC_PATH = "/stac"

# Catalog identity from the live landing page.
CATALOG_ID = "honua-stac-catalog"
CATALOG_TITLE = "Honua STAC Catalog"
STAC_VERSION = "1.0.0"

# Conformance classes the landing page advertises and the QGIS catalog honours.
REQUIRED_CONFORMANCE = (
    "https://api.stacspec.org/v1.0.0/core",
    "https://api.stacspec.org/v1.0.0/collections",
    "https://api.stacspec.org/v1.0.0/item-search",
    "https://api.stacspec.org/v1.0.0/ogcapi-features",
)
ABSENT_CONFORMANCE = "https://example.invalid/not-a-conformance-class"

# Collections seeded by client-compat-v1.sql. The live list is a superset: the
# OWSLib lane adds transient per-test layers (10, 11, 12), so a count assertion
# here would be flaky by construction.
SEEDED_COLLECTION_IDS = frozenset({"0", "2000", "2001", "2002", "3000"})
CERTIFICATION_COLLECTION = "0"

# The seeded envelope around items 1, 2 and 3, and the ids it must select.
SEARCH_BBOX = (-122.495, 37.705, -122.455, 37.735)
SEARCH_BBOX_IDS = ["1", "2", "3"]
# Mid-Atlantic: inside no seeded extent.
EMPTY_BBOX = (0.0, 0.0, 1.0, 1.0)
SEARCH_BY_ID = ["2", "4"]
# Items seeded into the certification collection. Every search below is scoped to
# that collection: an unscoped /search spans the transient per-test collections the
# OWSLib lane creates, which pushes numberMatched to 20 and makes ids ambiguous.
TOTAL_SEEDED_ITEMS = 10

# Item 1's single asset, as served.
ASSET_ITEM_ID = "1"
ASSET_KEY = "geojson"
ASSET_MEDIA_TYPE = "application/geo+json"
ASSET_ROLES = ["data"]
ASSET_TITLE = "GeoJSON"
ASSET_PATH = "/ogc/features/collections/0/items/1"
MISSING_ITEM_PATH = "/ogc/features/collections/0/items/999999"


def _http_get(url: str):
    """GET ``url`` through QGIS's own blocking network stack.

    Returns ``(error_code, http_status, body_bytes, content_type)``.
    """
    from qgis.core import QgsBlockingNetworkRequest
    from qgis.PyQt.QtCore import QUrl
    from qgis.PyQt.QtNetwork import QNetworkRequest

    request = QgsBlockingNetworkRequest()
    code = request.get(QNetworkRequest(QUrl(url)))
    reply = request.reply()
    status = reply.attribute(QNetworkRequest.HttpStatusCodeAttribute)
    content_type = bytes(reply.rawHeader(b"Content-Type")).decode("ascii", "replace")
    return code, status, bytes(reply.content()), content_type


def _parse_stac(url: str, body: bytes):
    """Feed ``body`` to QGIS's STAC parser with ``url`` as the base for relative links."""
    from qgis.core import QgsStacParser
    from qgis.PyQt.QtCore import QByteArray, QUrl

    parser = QgsStacParser()
    parser.setBaseUrl(QUrl(url))
    parser.setData(QByteArray(body))
    return parser


@pytest.mark.integration
@pytest.mark.pyqgis
class TestStacClientCompat:
    """STAC API 1.0.0 via the QGIS core STAC classes."""

    # ------------------------------------------------------------------
    # catalog-landing -> CERT-CONN-01
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-CONN-01")
    def test_catalog_landing_parses_as_a_stac_catalog(
        self, qgis_app, base_url: str, stac_evidence: CertificationEvidenceCollector
    ) -> None:
        """QGIS parses the landing page into a QgsStacCatalog with its conformance."""
        from qgis.core import Qgis

        started = time.monotonic()
        landing_url = f"{base_url}{STAC_PATH}"

        code, status, body, content_type = _http_get(landing_url)
        assert int(code) == 0, (
            f"QGIS could not fetch the landing page {landing_url}: "
            f"error code {int(code)}, HTTP {status}"
        )
        assert status == 200, f"landing page answered HTTP {status}, expected 200"
        assert body, "landing page returned an empty body"

        parser = _parse_stac(landing_url, body)
        assert parser.type() == Qgis.StacObjectType.Catalog, (
            "QGIS's STAC parser did not recognise the landing page as a Catalog; "
            f"it reported {parser.type()!r} (parse error: {parser.error()!r})"
        )
        catalog = parser.catalog()
        assert catalog is not None, (
            f"QgsStacParser.catalog() returned None: {parser.error()!r}"
        )

        assert catalog.id() == CATALOG_ID, (
            f"catalog id was {catalog.id()!r}, expected {CATALOG_ID!r}"
        )
        assert catalog.title() == CATALOG_TITLE, (
            f"catalog title was {catalog.title()!r}, expected {CATALOG_TITLE!r}"
        )
        assert catalog.stacVersion() == STAC_VERSION, (
            f"catalog stac_version was {catalog.stacVersion()!r}, "
            f"expected {STAC_VERSION!r}"
        )
        assert catalog.description(), "catalog carried no description"

        # Known-good conformance classes first, so the negative below cannot pass
        # merely because conformsTo() always answers False.
        for conformance_class in REQUIRED_CONFORMANCE:
            assert catalog.conformsTo(conformance_class), (
                f"the QGIS catalog does not report conformance to {conformance_class}, "
                "which the landing page advertises"
            )
        assert not catalog.conformsTo(ABSENT_CONFORMANCE), (
            "conformsTo() answered True for a conformance class the server never "
            f"advertises ({ABSENT_CONFORMANCE}), so it is not actually reading the "
            "conformsTo array"
        )

        # The catalog must expose its children and resolve its own root. The
        # children are judged against the origin the landing page advertises for
        # itself (its "self" link), not against the transport URL this lane dialled:
        # a fixture may legitimately publish one public identity while being reached
        # through another, and a STAC client follows advertised links, so what
        # matters is that they are self-consistent and that they resolve.
        self_hrefs = [
            link.href() for link in catalog.links() if link.relation() == "self"
        ]
        assert len(self_hrefs) == 1, (
            f"expected exactly one self link on the landing page, got {self_hrefs}"
        )
        advertised_root = self_hrefs[0].rstrip("/")
        child_hrefs = [
            link.href() for link in catalog.links() if link.relation() == "child"
        ]
        assert child_hrefs, "the catalog advertised no child collection links"
        collections_prefix = f"{advertised_root}/collections/"
        assert all(href.startswith(collections_prefix) for href in child_hrefs), (
            f"not every child link sits under {collections_prefix}: {child_hrefs}"
        )
        # Self-consistency alone could be satisfied by links that point nowhere, so
        # the first advertised child must actually answer through QGIS's stack.
        child_code, child_status, child_body, _ = _http_get(child_hrefs[0])
        assert int(child_code) == 0 and child_status == 200 and child_body, (
            f"the advertised child link {child_hrefs[0]} did not resolve: "
            f"error {int(child_code)}, HTTP {child_status}"
        )
        assert catalog.rootUrl().rstrip("/") == landing_url.rstrip("/"), (
            f"catalog rootUrl was {catalog.rootUrl()!r}, expected {landing_url!r}"
        )

        rels = {link.relation() for link in catalog.links()}
        for required_rel in ("self", "root", "data", "conformance", "search"):
            assert required_rel in rels, (
                f"the catalog is missing the {required_rel!r} link relation; "
                f"QGIS parsed {sorted(rels)}"
            )

        stac_evidence.record(
            "CERT-CONN-01", "pass",
            duration_ms=int((time.monotonic() - started) * 1000),
            measured_count=len(child_hrefs),
            notes=(
                f"QgsBlockingNetworkRequest + QgsStacParser parsed {landing_url} as a "
                f"QgsStacCatalog id={CATALOG_ID!r} stac_version={STAC_VERSION}, "
                f"advertising {len(child_hrefs)} child collections and honouring "
                f"{len(REQUIRED_CONFORMANCE)} conformance classes while rejecting an "
                "unadvertised one. The controller's own object path is unusable from "
                "Python: fetchStacObjectAsync has no bound takeStacObject in 3.44.14."
            ),
            evidence_ref=landing_url,
        )

    # ------------------------------------------------------------------
    # collections -> CERT-DISC-01
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-DISC-01")
    def test_collections_are_listed_and_individually_resolvable(
        self, qgis_app, base_url: str, stac_evidence: CertificationEvidenceCollector
    ) -> None:
        """QgsStacController.fetchCollections lists collections that each resolve."""
        from qgis.core import Qgis, QgsStacController
        from qgis.PyQt.QtCore import QUrl

        started = time.monotonic()
        collections_url = f"{base_url}{STAC_PATH}/collections"
        controller = QgsStacController()

        listing = controller.fetchCollections(QUrl(collections_url))
        assert listing is not None, (
            f"fetchCollections({collections_url}) returned None, so the QGIS STAC "
            "client could not fetch or parse the collection list"
        )
        # Counters must be read before takeCollections() empties the container.
        returned = listing.numberReturned()
        collections = listing.takeCollections()
        assert collections, "the collection list parsed but held no collections"
        assert returned == len(collections), (
            f"numberReturned was {returned} but {len(collections)} collections were "
            "parsed"
        )

        ids = {collection.id() for collection in collections}
        missing = SEEDED_COLLECTION_IDS - ids
        assert not missing, (
            f"the collection list is missing seeded collection(s) {sorted(missing)}; "
            f"QGIS parsed {sorted(ids)}"
        )

        by_id = {collection.id(): collection for collection in collections}
        for collection_id in sorted(SEEDED_COLLECTION_IDS):
            collection = by_id[collection_id]
            assert collection.type() == Qgis.StacObjectType.Collection, (
                f"collection {collection_id} parsed as {collection.type()!r} "
                "rather than a Collection"
            )
            assert collection.stacVersion() == STAC_VERSION, (
                f"collection {collection_id} reported stac_version "
                f"{collection.stacVersion()!r}, expected {STAC_VERSION!r}"
            )
            assert collection.license(), (
                f"collection {collection_id} carried no license, which STAC requires"
            )
            spatial = collection.extent().spatialExtent()
            # A STAC 2D bbox parses into a zero-depth QgsBox3D, whose isEmpty()
            # is True on the z range alone - so the footprint is checked in x/y.
            assert spatial is not None, (
                f"collection {collection_id} has no spatial extent"
            )
            assert spatial.width() > 0 and spatial.height() > 0, (
                f"collection {collection_id} has a degenerate spatial extent "
                f"{spatial.toString(4)}"
            )
            rels = {link.relation() for link in collection.links()}
            for required_rel in ("self", "items"):
                assert required_rel in rels, (
                    f"collection {collection_id} is missing the {required_rel!r} link; "
                    f"QGIS parsed {sorted(rels)}"
                )

        # The advertised collection must actually resolve on its own, parsed by
        # QGIS as a Collection rather than as anything else.
        single_url = f"{collections_url}/{CERTIFICATION_COLLECTION}"
        code, status, body, _ = _http_get(single_url)
        assert int(code) == 0 and status == 200, (
            f"the advertised collection {single_url} did not resolve: "
            f"error {int(code)}, HTTP {status}"
        )
        single = _parse_stac(single_url, body)
        assert single.type() == Qgis.StacObjectType.Collection, (
            f"{single_url} did not parse as a STAC Collection; QGIS reported "
            f"{single.type()!r} ({single.error()!r})"
        )

        # Negative, after the known-good listing above: a collection path the
        # server does not serve must not yield a collection list.
        bogus_url = f"{collections_url}-nope-xyz"
        assert controller.fetchCollections(QUrl(bogus_url)) is None, (
            "fetchCollections returned a collection list for a URL the server does "
            f"not serve ({bogus_url}), so a listing here proves nothing"
        )

        stac_evidence.record(
            "CERT-DISC-01", "pass",
            duration_ms=int((time.monotonic() - started) * 1000),
            measured_count=len(collections),
            notes=(
                f"QgsStacController.fetchCollections({collections_url}) returned "
                f"{len(collections)} collections (numberReturned={returned}), covering "
                f"all seeded ids {sorted(SEEDED_COLLECTION_IDS)}; each carries a "
                "license, a 1.0.0 stac_version, a non-empty spatial extent and "
                "self/items links, collection "
                f"{CERTIFICATION_COLLECTION!r} re-resolved standalone as a Collection, "
                "and an unserved collections URL returned None. The live list is a "
                "superset of the seeded ids because the OWSLib lane adds transient "
                "per-test collections."
            ),
            evidence_ref=collections_url,
        )

    # ------------------------------------------------------------------
    # item-search -> CERT-QFLT-01
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-QFLT-01")
    def test_item_search_honours_bbox_ids_and_datetime(
        self, qgis_app, base_url: str, stac_evidence: CertificationEvidenceCollector
    ) -> None:
        """The QGIS STAC client issues item-search and the server narrows the result."""
        from qgis.core import QgsRectangle, QgsStacController
        from qgis.PyQt.QtCore import QUrl

        started = time.monotonic()
        search_url = f"{base_url}{STAC_PATH}/search"
        controller = QgsStacController()

        scope = f"collections={CERTIFICATION_COLLECTION}"

        def search(query: str):
            """Run one collection-scoped item-search.

            Returns (ids, matched, returned, next_url, items).
            """
            result = controller.fetchItemCollection(
                QUrl(f"{search_url}?{scope}&{query}")
            )
            if result is None:
                return None
            matched = result.numberMatched()
            returned = result.numberReturned()
            next_url = result.nextUrl().toString()
            # takeItems(), never items(): the borrowed variant crashes the
            # interpreter when the collection is collected.
            items = result.takeItems()
            return [item.id() for item in items], matched, returned, next_url, items

        # Unfiltered search establishes the baseline the filters narrow from.
        unfiltered = search("limit=3")
        assert unfiltered is not None, (
            f"fetchItemCollection({search_url}?{scope}&limit=3) returned None, so the "
            "QGIS STAC client could not issue item-search at all"
        )
        ids, matched, returned, next_url, _ = unfiltered
        assert matched == TOTAL_SEEDED_ITEMS, (
            f"unfiltered item-search reported numberMatched={matched}, expected "
            f"{TOTAL_SEEDED_ITEMS}"
        )
        assert len(ids) == 3 and returned == 3, (
            f"limit=3 returned {len(ids)} items (numberReturned={returned})"
        )
        assert next_url, "a paged item-search advertised no next link"

        # bbox narrows to the three items inside the envelope, and QGIS's own
        # parsed geometries must agree with the server's selection.
        xmin, ymin, xmax, ymax = SEARCH_BBOX
        bbox_result = search(f"bbox={xmin},{ymin},{xmax},{ymax}")
        assert bbox_result is not None, "the bbox item-search returned None"
        bbox_ids, bbox_matched, _, _, bbox_items = bbox_result
        assert bbox_ids == SEARCH_BBOX_IDS, (
            f"bbox item-search returned {bbox_ids}, expected {SEARCH_BBOX_IDS}"
        )
        assert bbox_matched == len(SEARCH_BBOX_IDS), (
            f"bbox item-search reported numberMatched={bbox_matched}, expected "
            f"{len(SEARCH_BBOX_IDS)}"
        )
        assert bbox_matched < TOTAL_SEEDED_ITEMS, (
            "the bbox search matched every seeded item, so it did not filter anything"
        )
        envelope = QgsRectangle(xmin, ymin, xmax, ymax)
        for item in bbox_items:
            geometry = item.geometry()
            assert geometry is not None and not geometry.isNull(), (
                f"item {item.id()} came back from a spatial search with no geometry"
            )
            point = geometry.asPoint()
            assert envelope.contains(point), (
                f"item {item.id()} at {point.toString(6)} is outside the requested "
                f"bbox {SEARCH_BBOX}"
            )

        # ids selects exactly the requested items.
        id_result = search(f"ids={','.join(SEARCH_BY_ID)}")
        assert id_result is not None, "the ids item-search returned None"
        assert id_result[0] == SEARCH_BY_ID, (
            f"ids item-search returned {id_result[0]}, expected {SEARCH_BY_ID}"
        )

        # datetime narrows temporally. Asserted over the wire because
        # QgsStacItem.dateTime() is unusable in 3.44.14 (always invalid).
        window_result = search("datetime=2024-01-01T00:00:00Z/2024-01-03T23:59:59Z")
        assert window_result is not None, "the datetime item-search returned None"
        assert window_result[0] == SEARCH_BBOX_IDS, (
            f"datetime item-search returned {window_result[0]}, expected "
            f"{SEARCH_BBOX_IDS}"
        )
        assert all(not item.dateTime().isValid() for item in bbox_items), (
            "QgsStacItem.dateTime() became valid - if QGIS now parses STAC item "
            "datetimes, assert the instants directly instead of only over the wire"
        )

        # Negatives, after the known-good searches above.
        empty = search("bbox={},{},{},{}".format(*EMPTY_BBOX))
        assert empty is not None, (
            f"a search over an empty region {EMPTY_BBOX} failed outright instead of "
            "returning an empty collection"
        )
        assert empty[0] == [], (
            f"a bbox with no seeded items returned {empty[0]}, expected nothing"
        )
        assert controller.fetchItemCollection(
            QUrl(f"{search_url}?{scope}&bbox=1,2,3")
        ) is None, (
            "a malformed 3-value bbox yielded an item collection instead of failing"
        )

        stac_evidence.record(
            "CERT-QFLT-01", "pass",
            duration_ms=int((time.monotonic() - started) * 1000),
            measured_count=len(SEARCH_BBOX_IDS),
            notes=(
                "QgsStacController.fetchItemCollection issued GET item-search against "
                f"{search_url} scoped to collection {CERTIFICATION_COLLECTION!r}: "
                f"limit=3 returned 3 of numberMatched={matched} with a "
                f"next link, bbox={SEARCH_BBOX} narrowed to {SEARCH_BBOX_IDS} with "
                "every QGIS-parsed geometry inside the envelope, "
                f"ids={SEARCH_BY_ID} selected exactly those, and a closed RFC 3339 "
                f"interval selected {SEARCH_BBOX_IDS}. An empty region returned no "
                "items and a 3-value bbox returned None. fetchItemCollection is "
                "blocking, so no Qt event loop is involved."
            ),
            evidence_ref=search_url,
        )

    # ------------------------------------------------------------------
    # asset-download -> CERT-RNDR-URL-01
    # ------------------------------------------------------------------
    @pytest.mark.cert("CERT-RNDR-URL-01")
    def test_item_asset_resolves_and_downloads(
        self, qgis_app, base_url: str, stac_evidence: CertificationEvidenceCollector
    ) -> None:
        """A STAC asset QGIS parsed is downloaded through QGIS and opens as a layer."""
        from qgis.core import QgsStacController, QgsVectorLayer
        from qgis.PyQt.QtCore import QUrl

        started = time.monotonic()
        search_url = f"{base_url}{STAC_PATH}/search"
        controller = QgsStacController()

        # Scoped to the certification collection: an unscoped ids search also
        # matches same-id items in the OWSLib lane's transient collections.
        result = controller.fetchItemCollection(
            QUrl(f"{search_url}?collections={CERTIFICATION_COLLECTION}"
                 f"&ids={ASSET_ITEM_ID}")
        )
        assert result is not None, (
            f"could not fetch item {ASSET_ITEM_ID} to read its assets"
        )
        items = result.takeItems()
        assert len(items) == 1, f"expected exactly one item, got {len(items)}"
        item = items[0]
        assert item.id() == ASSET_ITEM_ID

        # The STAC asset metadata QGIS parsed is what drives the download.
        assets = item.assets()
        assert set(assets) == {ASSET_KEY}, (
            f"item {ASSET_ITEM_ID} exposed assets {sorted(assets)}, expected "
            f"{[ASSET_KEY]}"
        )
        asset = assets[ASSET_KEY]
        # The href's path is pinned; its origin is whatever public identity the
        # fixture advertises, which need not be the transport URL this lane dialled.
        # The download below proves the advertised origin actually resolves.
        asset_path = urllib.parse.urlsplit(asset.href()).path
        assert asset_path == ASSET_PATH, (
            f"asset href was {asset.href()!r}, expected its path to be {ASSET_PATH!r}"
        )
        assert asset.mediaType() == ASSET_MEDIA_TYPE, (
            f"asset media type was {asset.mediaType()!r}, expected "
            f"{ASSET_MEDIA_TYPE!r}"
        )
        assert asset.roles() == ASSET_ROLES, (
            f"asset roles were {asset.roles()!r}, expected {ASSET_ROLES!r}"
        )
        assert asset.title() == ASSET_TITLE, (
            f"asset title was {asset.title()!r}, expected {ASSET_TITLE!r}"
        )

        # QGIS offers no STAC-native handle for a non-cloud-optimized asset, so
        # the download below is deliberately QGIS's generic network stack driven
        # by the parsed href. Recorded as an assertion so the limitation is
        # evidence rather than a silent choice.
        assert not asset.isCloudOptimized(), (
            f"{ASSET_MEDIA_TYPE} is reported cloud-optimized - if QGIS now hands out "
            "a provider uri for it, certify the uri()/uris() path instead"
        )
        assert item.uris() == [], (
            "QgsStacItem.uris() is non-empty, so a STAC-native asset handle now "
            "exists and should be certified directly"
        )

        # Download the asset through QGIS.
        code, status, body, content_type = _http_get(asset.href())
        assert int(code) == 0, (
            f"QGIS could not download the asset {asset.href()}: error {int(code)}, "
            f"HTTP {status}"
        )
        assert status == 200, f"asset download answered HTTP {status}, expected 200"
        assert body, "asset download returned an empty body"
        assert content_type.startswith(ASSET_MEDIA_TYPE), (
            f"asset served Content-Type {content_type!r}, expected "
            f"{ASSET_MEDIA_TYPE!r} as the asset advertised"
        )

        # The bytes are the advertised feature, not some other resource.
        payload = json.loads(body.decode("utf-8"))
        assert str(payload.get("id")) == ASSET_ITEM_ID, (
            f"the downloaded asset carries id {payload.get('id')!r}, expected "
            f"{ASSET_ITEM_ID!r}"
        )

        # And QGIS can actually open what it downloaded.
        download_dir = tempfile.mkdtemp(prefix="stac-asset-")
        asset_path = os.path.join(download_dir, "asset.geojson")
        with open(asset_path, "wb") as handle:
            handle.write(body)
        layer = QgsVectorLayer(asset_path, "stac-asset", "ogr")
        assert layer.isValid(), (
            f"QGIS could not open the downloaded asset as a layer: "
            f"{layer.error().summary()}"
        )
        assert layer.featureCount() == 1, (
            f"the downloaded asset opened with {layer.featureCount()} features, "
            "expected 1"
        )

        # Negative, after the known-good download above: an asset href the server
        # does not serve must fail rather than quietly yield bytes.
        missing_code, missing_status, _, _ = _http_get(f"{base_url}{MISSING_ITEM_PATH}")
        assert missing_status == 404, (
            f"an unserved asset href answered HTTP {missing_status}, expected 404"
        )
        assert int(missing_code) != 0, (
            "QGIS reported success for an asset href that answered 404"
        )

        stac_evidence.record(
            "CERT-RNDR-URL-01", "pass",
            duration_ms=int((time.monotonic() - started) * 1000),
            measured_count=layer.featureCount(),
            notes=(
                f"Item {ASSET_ITEM_ID} exposed asset {ASSET_KEY!r} "
                f"(media={ASSET_MEDIA_TYPE}, roles={ASSET_ROLES}) through QgsStacItem."
                f"assets(); QGIS downloaded {asset.href()} with QgsBlockingNetworkRequest "
                f"(HTTP 200, {len(body)} bytes, Content-Type {content_type}), the payload "
                f"was the advertised feature id {ASSET_ITEM_ID}, and QgsVectorLayer opened "
                "it with 1 feature. An unserved href answered 404 and QGIS surfaced the "
                "error. QGIS core has no STAC-native download API: QgsStacAsset.uri() is "
                "populated only for cloud-optimized assets, and this asset is "
                f"{ASSET_MEDIA_TYPE}, so uri()/uris() are empty and the parsed href drives "
                "the transfer."
            ),
            evidence_ref=asset.href(),
        )
