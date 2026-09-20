# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""GeoServices REST FeatureServer compatibility through the real QGIS client.

QGIS 3.44.14 LTR ships an ``arcgisfeatureserver`` vector data provider (confirmed
present in ``QgsProviderRegistry.instance().providerList()`` alongside
``arcgismapserver`` and ``arcgisvectortileservice``). This module certifies the
honua FeatureServer surface through that provider, one case per checklist
operation, so a cell closes on client evidence rather than on the server's own
conformance tests.

Only four of the ten tracked operations turn out to be reachable from this
client. The rest are recorded as ``not-applicable`` or ``skip`` with the probe
that establishes it, because a certification lane that invents a test for an
operation no client can issue is worse than an open cell.

What the provider actually puts on the wire
-------------------------------------------
Established by proxying the provider through a logging HTTP proxy rather than by
reading binary strings - ``strings(1)`` gave a false negative here, reporting no
``esriSpatialRel`` in any shipped QGIS library while spatial filtering
demonstrably works, so string absence proves nothing and is not used as evidence
anywhere below.

* layer load -> ``GET /FeatureServer/<id>?f=json`` (plus a
  ``/rest/admin/services/...`` probe that 404s harmlessly)
* feature ids -> ``GET .../query?where=1=1&returnIdsOnly=true``
* attributes   -> ``GET .../query?objectIds=...&outFields=*&returnGeometry=true``
* subset string-> the QGIS expression is passed through as ``where=``
* spatial      -> ``geometryType=esriGeometryEnvelope`` with
  ``spatialRel=esriSpatialRelEnvelopeIntersects``
* edits        -> ``POST .../addFeatures`` / ``updateFeatures`` /
  ``deleteFeatures`` (the per-operation endpoints, never the combined
  ``applyEdits``)

Notably it never issues ``outStatistics``, ``queryRelatedRecords``,
``queryAttachments`` or ``createReplica``, and it never reads the service root
``/FeatureServer?f=json`` on the layer path - only the QGIS Browser does, which
is why ``service-info`` is certified through the ``AFS`` data item provider.

The QGIS 3.44 manual agrees: section 11.1.7.3 "Using ArcGIS REST Servers"
(https://docs.qgis.org/3.44/en/docs/user_manual/managing_data_source/opening_data.html)
documents browsing the service tree, loading layers, attribute filters via the
expression builder and an "only request features overlapping the current view
extent" option, and mentions attachments, related records, replica/sync, field
domains and server-side statistics nowhere at all.

Two provider behaviours are load-bearing
----------------------------------------
A provider builds an INVALID layer rather than raising on most errors, so
``isValid()`` is asserted everywhere and ``layer.error().summary()`` is printed
in the failure message.

Worse, ``addFeatures()`` returned ``True`` with an empty error summary while the
server was answering ``{"error":{"code":499,...}}`` - an unauthenticated edit
looked like a success. Every edit assertion below is therefore verified against
an independent HTTP read of the layer rather than against the provider's own
return value.
"""

from __future__ import annotations

import contextlib
import http.server
import os
import socketserver
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

import httpx
import pytest

from .conftest import CertificationEvidenceCollector

# test_service layer 0 carries the rich schema (14 non-geometry fields, a
# status field with two distinct values, timeInfo) and is editable.
SERVICE_ID = "test_service"
LAYER_ID = "0"

# browser_compat carries the seeded points used for the spatial identify.
POINT_SERVICE_ID = "browser_compat"
POINT_LAYER_ID = "2000"

# A seeded browser_compat point, so identify is exercised somewhere a feature
# actually is.
KNOWN_FEATURE_NAME = "pt-alpha"
KNOWN_FEATURE_OID = 13
KNOWN_FEATURE_LON = -122.4194
KNOWN_FEATURE_LAT = 37.7749

# Tight enough to exclude pt-beta at (-122.418, 37.776), which is what makes the
# spatial case prove the envelope is honoured rather than ignored.
IDENTIFY_XMIN = -122.4200
IDENTIFY_XMAX = -122.4188
IDENTIFY_YMIN = 37.7743
IDENTIFY_YMAX = 37.7755

# Nowhere near any seeded feature.
EMPTY_XMIN, EMPTY_YMIN, EMPTY_XMAX, EMPTY_YMAX = -121.0, 36.0, -120.9, 36.1

EXPECTED_FEATURE_COUNT = 10
ACTIVE_FILTER = "status = 'active'"
EXPECTED_ACTIVE_COUNT = 5

# test_service layer 0, field "count", values 1..10.
EXPECTED_COUNT_MIN = 1
EXPECTED_COUNT_MAX = 10
EXPECTED_COUNT_SUM = 55.0
EXPECTED_STATUS_VALUES = ["active", "inactive"]

# The scratch layer the WFS-T lane already uses for inserts; safe to write to.
EDIT_LAYER_ID = "10"

# QGIS stores ArcGIS REST browser connections under this settings key. Found by
# probing candidate spellings against QgsArcGisRestRootItem.createChildren():
# the legacy "qgis/connections-arcgisfeatureserver/<name>/url" spelling yields
# no children on 3.44, the settings-tree spelling below does.
CONNECTION_SETTINGS_KEY = "connections/arcgisfeatureserver/items/{name}/url"
CONNECTION_NAME = "honua-cert"


def make_featureserver_layer(
    base_url: str,
    service_id: str,
    layer_id: str,
    *,
    crs: str = "EPSG:4326",
    authcfg: str = "",
    host: str | None = None,
):
    """Construct a QGIS vector layer via the stock arcgisfeatureserver provider.

    The provider wants the *layer* endpoint, not the service endpoint: given
    ``.../FeatureServer`` it fetches the service document and then builds an
    invalid layer, because a service is not a layer. ``crs`` is supplied because
    a bare ``url=`` also works but leaves the layer CRS to be inferred.
    """
    from qgis.core import QgsVectorLayer

    root = host or base_url
    endpoint = f"{root}/rest/services/{service_id}/FeatureServer/{layer_id}"
    uri = f"crs='{crs}' url='{endpoint}'"
    if authcfg:
        uri = f"{uri} authcfg={authcfg}"
    return QgsVectorLayer(uri, f"afs_{service_id}_{layer_id}", "arcgisfeatureserver")


# ---------------------------------------------------------------------------
# Wire capture
# ---------------------------------------------------------------------------
#
# Several cells can only be decided by what the provider puts on the wire: that
# a where= clause really is pushed down rather than applied locally, and that no
# outStatistics request is ever issued. A logging pass-through proxy in front of
# the server is the only way to see that from inside PyQGIS.
#
# The port is ephemeral, which also defeats QGIS's network cache: each test gets
# a distinct origin, so no request can be served from a previous test's cache
# and quietly disappear from the capture.


class _CapturingProxy(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    upstream = ""
    captured: list[str] = []

    def log_message(self, *args):  # keep pytest output clean
        return

    def _forward(self, body: bytes | None) -> None:
        headers = {
            key: value for key, value in self.headers.items()
            # Accept-Encoding must be dropped: urllib does not decompress, and
            # handing QGIS a gzip body it did not ask for makes the layer fail
            # to build with an empty error summary.
            if key.lower() not in ("host", "accept-encoding")
        }
        entry = f"{self.command} {urllib.parse.unquote(self.path)}"
        if body:
            entry += f" BODY={urllib.parse.unquote(body.decode('utf-8', 'replace'))}"
        type(self).captured.append(entry)

        request = urllib.request.Request(
            self.upstream + self.path, data=body, method=self.command,
            headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                payload = response.read()
                self.send_response(response.status)
                content_type = response.headers.get("Content-Type")
                if content_type:
                    self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
        except urllib.error.HTTPError as error:
            payload = error.read()
            self.send_response(error.code)
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
        except Exception:  # noqa: BLE001 - a dead proxy must not hang the lane
            self.send_response(502)
            self.send_header("Content-Length", "0")
            self.end_headers()

    def do_GET(self) -> None:
        self._forward(None)

    def do_POST(self) -> None:
        length = int(self.headers.get("Content-Length") or 0)
        self._forward(self.rfile.read(length) if length else b"")


class _ThreadingServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


@contextlib.contextmanager
def wire_capture(base_url: str):
    """Serve ``base_url`` through a logging proxy; yield (host, captured)."""
    captured: list[str] = []
    handler = type(
        "_Handler", (_CapturingProxy,),
        {"upstream": base_url, "captured": captured},
    )
    server = _ThreadingServer(("127.0.0.1", 0), handler)
    port = server.server_address[1]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    host = f"http://127.0.0.1:{port}"
    try:
        # Prove the proxy forwards before any assertion depends on it. A dead
        # proxy would otherwise make every "no such request was issued" check
        # pass for free, which is precisely the failure mode these captures
        # exist to rule out.
        probe = httpx.get(f"{host}/rest/info", params={"f": "json"}, timeout=30)
        assert probe.status_code == 200 and "currentVersion" in probe.text, (
            f"the capturing proxy did not forward to the server: "
            f"{probe.status_code} {probe.text[:200]!r}"
        )
        assert captured, "the capturing proxy recorded nothing for its own probe"
        captured.clear()
        yield host, captured
    finally:
        server.shutdown()
        server.server_close()


def _layer_document(base_url: str, service_id: str, layer_id: str) -> dict:
    response = httpx.get(
        f"{base_url}/rest/services/{service_id}/FeatureServer/{layer_id}",
        params={"f": "json"}, timeout=30)
    response.raise_for_status()
    return response.json()


def _service_document(base_url: str, service_id: str) -> dict:
    response = httpx.get(
        f"{base_url}/rest/services/{service_id}/FeatureServer",
        params={"f": "json"}, timeout=30)
    response.raise_for_status()
    return response.json()


@pytest.mark.integration
@pytest.mark.pyqgis
class TestFeatureServerClientCompat:
    """FeatureServer 10.8 via the QGIS arcgisfeatureserver provider."""

    def _layer(self, base_url: str, service_id: str = SERVICE_ID,
               layer_id: str = LAYER_ID, **kwargs):
        return make_featureserver_layer(base_url, service_id, layer_id, **kwargs)

    # ------------------------------------------------------------------
    # service-info / CERT-DISC-01
    # ------------------------------------------------------------------
    #
    # The layer path never reads the service root, so this is certified through
    # the AFS data item provider - the QGIS Browser path - which walks
    # /rest/services?f=json and then /<service>/FeatureServer?f=json and turns
    # the result into addable layer items.
    def test_service_info_enumerates_the_feature_service_layers(
        self, qgis_app, base_url: str,
        featureserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        from qgis.core import QgsProviderRegistry, QgsSettings

        started = time.monotonic()
        published = _service_document(base_url, SERVICE_ID)
        expected = [layer["name"] for layer in published["layers"]]
        assert expected, "the FeatureServer service document advertises no layers"

        settings = QgsSettings()
        settings.setValue(
            CONNECTION_SETTINGS_KEY.format(name=CONNECTION_NAME),
            f"{base_url}/rest/services")
        settings.sync()

        metadata = QgsProviderRegistry.instance().providerMetadata(
            "arcgisfeatureserver")
        providers = metadata.dataItemProviders()
        assert providers, "arcgisfeatureserver exposes no data item provider"

        # References are held so SIP does not collect a parent while its
        # children are still in use.
        root = providers[0].createDataItem("", None)
        assert root is not None, "the AFS data item provider produced no root item"
        connections = root.createChildren()
        assert connections, (
            "the AFS browser root enumerated no connections, so this case would "
            f"prove nothing: wrote {CONNECTION_SETTINGS_KEY.format(name=CONNECTION_NAME)!r}"
        )

        matching = [item for item in connections if item.name() == CONNECTION_NAME]
        assert matching, (
            f"the stored connection {CONNECTION_NAME!r} did not appear; got "
            f"{[item.name() for item in connections]}"
        )
        services = matching[0].createChildren()
        assert services, "the connection enumerated no services from /rest/services"

        # Each service name appears once per service type (FeatureServer,
        # MapServer, ImageServer) and the items are identically named, so the
        # FeatureServer one is identified by the endpoint its children carry.
        feature_service_layers = []
        for service in services:
            if service.name() != SERVICE_ID:
                continue
            children = service.createChildren()
            if children and all(
                "/FeatureServer/" in child.uri() for child in children
            ):
                feature_service_layers.append(children)

        assert len(feature_service_layers) == 1, (
            f"expected exactly one FeatureServer collection for {SERVICE_ID!r}, "
            f"found {len(feature_service_layers)}"
        )
        layers = feature_service_layers[0]
        names = [item.name() for item in layers]
        assert names == expected, (
            "the browser did not enumerate the layers the service document "
            f"publishes: advertised {expected}, enumerated {names}"
        )
        featureserver_evidence.record(
            "CERT-DISC-01", "pass",
            duration_ms=int((time.monotonic() - started) * 1000),
            measured_count=len(names),
            notes=(
                "AFS data item provider read /rest/services and "
                f"/{SERVICE_ID}/FeatureServer?f=json and enumerated {names}."
            ),
            evidence_ref=f"{base_url}/rest/services/{SERVICE_ID}/FeatureServer",
        )

    # ------------------------------------------------------------------
    # layer-metadata / CERT-SCHM-01
    # ------------------------------------------------------------------
    #
    # The client's field list is compared against the service's own layer
    # document rather than against a hardcoded list, so the case tracks the
    # fixture instead of silently drifting from it. The negative half asserts a
    # known-good sibling first: a provider that fails to build every layer would
    # otherwise satisfy the "unknown layer is invalid" half for free.
    def test_layer_metadata_matches_the_published_schema(
        self, qgis_app, base_url: str,
        featureserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        from qgis.core import QgsWkbTypes

        published = _layer_document(base_url, SERVICE_ID, LAYER_ID)
        expected_fields = [
            field["name"] for field in published["fields"]
            if field["type"] != "esriFieldTypeGeometry"
        ]

        layer = self._layer(base_url)
        assert layer.isValid(), (
            "the arcgisfeatureserver provider could not build a layer from the "
            f"FeatureServer layer document: {layer.error().summary()}"
        )

        assert [field.name() for field in layer.fields()] == expected_fields, (
            "the provider's field list does not match the published schema: "
            f"published {expected_fields}, provider "
            f"{[field.name() for field in layer.fields()]}"
        )
        assert QgsWkbTypes.geometryType(layer.wkbType()) == QgsWkbTypes.PointGeometry, (
            f"esriGeometryPoint did not map to a point layer: {layer.wkbType()}")
        assert layer.crs().isValid() and layer.crs().postgisSrid() == 4326, (
            f"the published wkid 4326 did not resolve: {layer.crs().authid()}")
        assert layer.featureCount() == EXPECTED_FEATURE_COUNT, (
            f"expected {EXPECTED_FEATURE_COUNT} features, got {layer.featureCount()}")

        extent = layer.extent()
        published_extent = published["extent"]
        assert extent.xMinimum() == pytest.approx(published_extent["xmin"], abs=1e-6)
        assert extent.yMaximum() == pytest.approx(published_extent["ymax"], abs=1e-6)

        # Negative half, guarded by the known-good layer asserted above.
        unknown = self._layer(base_url, layer_id="9999")
        assert not unknown.isValid(), (
            "an unpublished FeatureServer layer id produced a layer QGIS "
            "considers valid, so the client would show an empty table while "
            "reporting success"
        )
        featureserver_evidence.record(
            "CERT-SCHM-01", "pass",
            measured_count=len(expected_fields),
            notes=(
                f"Layer document parsed: {len(expected_fields)} fields, "
                "esriGeometryPoint -> point, wkid 4326, extent honoured; an "
                "unpublished layer id yielded an invalid layer."
            ),
        )

    # ------------------------------------------------------------------
    # query / CERT-QFLT-01
    # ------------------------------------------------------------------
    #
    # The point of this case is that the filter is evaluated by the server, not
    # by QGIS after downloading everything. Both halves are needed: the count
    # must drop, and a request carrying the where= clause must appear on the
    # wire. A client-side filter would satisfy the first half alone.
    def test_query_pushes_the_attribute_filter_to_the_server(
        self, qgis_app, base_url: str,
        featureserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        server_side = httpx.get(
            f"{base_url}/rest/services/{SERVICE_ID}/FeatureServer/{LAYER_ID}/query",
            params={"f": "json", "where": ACTIVE_FILTER, "returnCountOnly": "true"},
            timeout=30)
        server_side.raise_for_status()
        assert server_side.json()["count"] == EXPECTED_ACTIVE_COUNT, (
            f"the fixture no longer has {EXPECTED_ACTIVE_COUNT} active rows: "
            f"{server_side.json()}"
        )

        with wire_capture(base_url) as (host, captured):
            layer = self._layer(base_url, host=host)
            assert layer.isValid(), (
                f"the layer did not build through the capturing proxy: "
                f"{layer.error().summary()!r} captured={captured}"
            )
            assert layer.featureCount() == EXPECTED_FEATURE_COUNT, (
                f"unfiltered count was {layer.featureCount()}")

            assert layer.setSubsetString(ACTIVE_FILTER), (
                f"the provider rejected the subset string {ACTIVE_FILTER!r}")
            filtered = layer.featureCount()
            names = [feature["status"] for feature in layer.getFeatures()]
            layer.setSubsetString("")
            restored = layer.featureCount()

            pushed = [line for line in captured if "where=" + ACTIVE_FILTER in line]

        assert filtered == EXPECTED_ACTIVE_COUNT, (
            f"the filter returned {filtered} features, expected "
            f"{EXPECTED_ACTIVE_COUNT}"
        )
        assert set(names) == {"active"}, (
            f"the filtered features are not all active: {sorted(set(names))}")
        assert restored == EXPECTED_FEATURE_COUNT, (
            f"clearing the subset string left {restored} features")
        assert pushed, (
            "no request carried the where= clause, so QGIS filtered locally "
            "and the server's query operation was never exercised. Captured: "
            f"{captured}"
        )
        featureserver_evidence.record(
            "CERT-QFLT-01", "pass",
            measured_count=filtered,
            notes=(
                f"Subset string {ACTIVE_FILTER!r} was pushed down as a where= "
                f"query parameter and returned {filtered} of "
                f"{EXPECTED_FEATURE_COUNT} features."
            ),
            evidence_ref=pushed[0][:200],
        )

    # ------------------------------------------------------------------
    # identify / CERT-GEOM-01
    # ------------------------------------------------------------------
    #
    # FeatureServer has no /identify operation - that is a MapServer one, and
    # this server correctly 404s it. QGIS identifies a vector layer by issuing a
    # spatial query instead, which is what is certified here:
    # geometryType=esriGeometryEnvelope with spatialRel=...EnvelopeIntersects.
    #
    # The empty-box half is what makes it non-vacuous: without it a server that
    # ignores the envelope and returns everything would still "identify"
    # pt-alpha.
    def test_identify_resolves_the_seeded_feature_by_envelope(
        self, qgis_app, base_url: str,
        featureserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        from qgis.core import QgsFeatureRequest, QgsRectangle

        with wire_capture(base_url) as (host, captured):
            layer = self._layer(
                base_url, POINT_SERVICE_ID, POINT_LAYER_ID, host=host)
            assert layer.isValid(), (
                f"{POINT_SERVICE_ID}/{POINT_LAYER_ID} did not build: "
                f"{layer.error().summary()!r} captured={captured}"
            )

            hit_request = QgsFeatureRequest().setFilterRect(QgsRectangle(
                IDENTIFY_XMIN, IDENTIFY_YMIN, IDENTIFY_XMAX, IDENTIFY_YMAX))
            hits = [
                (feature["objectid"], feature["name"])
                for feature in layer.getFeatures(hit_request)
            ]

            empty_request = QgsFeatureRequest().setFilterRect(QgsRectangle(
                EMPTY_XMIN, EMPTY_YMIN, EMPTY_XMAX, EMPTY_YMAX))
            misses = list(layer.getFeatures(empty_request))

            spatial = [
                line for line in captured
                if "geometryType=esriGeometryEnvelope" in line
                and "spatialRel=esriSpatialRel" in line
            ]

        assert (KNOWN_FEATURE_OID, KNOWN_FEATURE_NAME) in hits, (
            f"an envelope around the seeded point did not identify "
            f"{KNOWN_FEATURE_NAME!r} (objectid {KNOWN_FEATURE_OID}); got {hits}"
        )
        assert not misses, (
            "an envelope containing no seeded feature still returned "
            f"{len(misses)} features, so the server ignores the geometry filter "
            "and identify cannot be relied on"
        )
        assert spatial, (
            "no request carried an esriGeometryEnvelope spatial filter, so the "
            f"envelope was applied client-side. Captured: {captured}"
        )
        featureserver_evidence.record(
            "CERT-GEOM-01", "pass",
            measured_count=len(hits),
            notes=(
                f"Envelope query identified {KNOWN_FEATURE_NAME!r} via "
                "esriGeometryEnvelope/esriSpatialRelEnvelopeIntersects; an "
                "empty envelope correctly returned nothing."
            ),
            evidence_ref=spatial[0][:200],
        )

    # ------------------------------------------------------------------
    # statistics / CERT-QFLT-02 -> not-applicable (no client path)
    # ------------------------------------------------------------------
    #
    # The server implements outStatistics, and QGIS returns correct statistics -
    # but it computes them locally from downloaded features and never asks the
    # server for them. Both facts are asserted so the not-applicable verdict is
    # attributed to the client rather than to a missing server feature, and the
    # capture is proven non-empty so "no outStatistics request" cannot pass by
    # virtue of no requests at all.
    def test_statistics_are_computed_client_side_not_delegated(
        self, qgis_app, base_url: str,
        featureserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        from qgis.core import QgsAggregateCalculator

        statistics_query = httpx.get(
            f"{base_url}/rest/services/{SERVICE_ID}/FeatureServer/{LAYER_ID}/query",
            params={
                "f": "json", "where": "1=1",
                "outStatistics": (
                    '[{"statisticType":"count","onStatisticField":"objectid",'
                    '"outStatisticFieldName":"n"}]'
                ),
            }, timeout=30)
        statistics_query.raise_for_status()
        server_statistics = statistics_query.json()
        assert server_statistics["features"][0]["attributes"]["n"] == (
            EXPECTED_FEATURE_COUNT
        ), f"the server's outStatistics answer changed: {server_statistics}"

        with wire_capture(base_url) as (host, captured):
            layer = self._layer(base_url, host=host)
            assert layer.isValid(), (
                f"the layer did not build through the proxy: "
                f"{layer.error().summary()!r} captured={captured}"
            )
            provider = layer.dataProvider()
            count_index = layer.fields().indexOf("count")
            status_index = layer.fields().indexOf("status")

            minimum = provider.minimumValue(count_index)
            maximum = provider.maximumValue(count_index)
            distinct = sorted(str(v) for v in provider.uniqueValues(status_index))
            total, ok = layer.aggregate(QgsAggregateCalculator.Sum, "count")

            delegated = [line for line in captured if "outStatistics" in line]
            downloaded = [line for line in captured if "outFields=*" in line]

        # The statistics QGIS reports are correct, so the calls really ran.
        assert minimum == EXPECTED_COUNT_MIN and maximum == EXPECTED_COUNT_MAX, (
            f"provider min/max were {minimum}/{maximum}")
        assert distinct == EXPECTED_STATUS_VALUES, f"unique values were {distinct}"
        assert ok and total == pytest.approx(EXPECTED_COUNT_SUM), (
            f"aggregate Sum returned {total} (ok={ok})")

        assert downloaded, (
            "the provider issued no attribute fetch, so this capture cannot "
            f"support any conclusion about statistics. Captured: {captured}"
        )
        assert not delegated, (
            "the provider DID issue an outStatistics request, so this operation "
            f"is client-reachable after all and must be certified: {delegated}"
        )
        featureserver_evidence.record(
            "CERT-QFLT-02", "not-applicable",
            measured_count=len(captured),
            notes=(
                "No client path: the server answers outStatistics correctly, but "
                "the arcgisfeatureserver provider computed min/max/unique/Sum "
                f"locally from a full outFields=* download across {len(captured)} "
                "captured requests and never issued outStatistics. QGIS 3.44 "
                "manual 11.1.7.3 documents no statistics support."
            ),
            evidence_ref="https://docs.qgis.org/3.44/en/docs/user_manual/managing_data_source/opening_data.html",
        )

    # ------------------------------------------------------------------
    # relatedRecords / CERT-DISC-02 -> not-applicable (no client path)
    # ------------------------------------------------------------------
    def test_related_records_have_no_client_path(
        self, qgis_app, base_url: str,
        featureserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = self._layer(base_url)
        assert layer.isValid(), (
            "the known-good layer does not resolve, so this capability probe "
            f"proves nothing: {layer.error().summary()}"
        )
        provider = layer.dataProvider()

        related_api = [
            name for name in dir(provider)
            if "relatedrecord" in name.lower() or "relationship" in name.lower()
        ]
        assert not related_api, (
            "the provider exposes a related-records API after all, so this "
            f"operation is client-reachable: {related_api}"
        )
        # discoverRelations is inherited from QgsVectorDataProvider and is
        # implemented only by the database providers; arcgisfeatureserver
        # returns an empty list.
        assert provider.discoverRelations(layer, [layer]) == []

        published = _layer_document(base_url, SERVICE_ID, LAYER_ID)
        featureserver_evidence.record(
            "CERT-DISC-02", "not-applicable",
            notes=(
                "No client path: the arcgisfeatureserver provider exposes no "
                "queryRelatedRecords or relationship API and "
                "discoverRelations() returns []; QGIS 3.44 manual 11.1.7.3 "
                "documents none. The fixture also publishes "
                f"relationships={published['relationships']} and "
                f"supportsQueryRelated={published['supportsQueryRelated']}."
            ),
            evidence_ref="QgsVectorDataProvider.discoverRelations() -> []",
        )

    # ------------------------------------------------------------------
    # attachments / CERT-CONN-02 -> not-applicable (no client path)
    # ------------------------------------------------------------------
    def test_attachments_have_no_client_path(
        self, qgis_app, base_url: str,
        featureserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = self._layer(base_url)
        assert layer.isValid(), (
            "the known-good layer does not resolve, so this capability probe "
            f"proves nothing: {layer.error().summary()}"
        )

        attachment_api = sorted(
            name for name in dir(layer.dataProvider()) + dir(layer)
            if "attachment" in name.lower()
        )
        assert not attachment_api, (
            "an attachment API is exposed after all, so this operation is "
            f"client-reachable: {attachment_api}"
        )

        published = _layer_document(base_url, SERVICE_ID, LAYER_ID)
        featureserver_evidence.record(
            "CERT-CONN-02", "not-applicable",
            notes=(
                "No client path: neither QgsVectorLayer nor the "
                "arcgisfeatureserver provider exposes any attachment API, and "
                "QGIS 3.44 manual 11.1.7.3 documents none. The fixture also "
                f"publishes hasAttachments={published['hasAttachments']} and "
                f"supportsQueryAttachments={published['supportsQueryAttachments']}."
            ),
            evidence_ref="dir(QgsVectorLayer)+dir(provider): no *attachment* member",
        )

    # ------------------------------------------------------------------
    # replica-sync / CERT-CONN-01 -> not-applicable (no client path)
    # ------------------------------------------------------------------
    def test_replica_sync_has_no_client_path(
        self, qgis_app, base_url: str,
        featureserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = self._layer(base_url)
        assert layer.isValid(), (
            "the known-good layer does not resolve, so this capability probe "
            f"proves nothing: {layer.error().summary()}"
        )

        replica_api = sorted(
            name for name in dir(layer.dataProvider())
            if "replica" in name.lower() or "synchroniz" in name.lower()
        )
        assert not replica_api, (
            "a replica/sync API is exposed after all, so this operation is "
            f"client-reachable: {replica_api}"
        )

        published = _service_document(base_url, SERVICE_ID)
        featureserver_evidence.record(
            "CERT-CONN-01", "not-applicable",
            notes=(
                "No client path: the arcgisfeatureserver provider exposes no "
                "createReplica or synchronizeReplica API - QGIS implements "
                "offline work through its own Offline Editing plugin, which "
                "copies features rather than creating a server replica - and "
                "QGIS 3.44 manual 11.1.7.3 documents no sync support. The "
                f"fixture also publishes syncEnabled={published['syncEnabled']}."
            ),
            evidence_ref="dir(provider): no *replica*/*synchroniz* member",
        )

    # ------------------------------------------------------------------
    # domains / CERT-SCHM-02 -> blocked on the fixture
    # ------------------------------------------------------------------
    # domains / CERT-SCHM-02
    # ------------------------------------------------------------------
    #
    # The seed publishes one coded-value domain, StatusDomain on
    # test_service/0.status. The QGIS arcgisfeatureserver provider turns a
    # coded-value domain into a ValueMap editor widget on the field, which is
    # the client-visible form of "the domain reached the client": the layer
    # document carries it, queryDomains answers it, and QGIS offers exactly the
    # seeded codes to an editor.
    def test_domains_reach_the_client_as_a_value_map(
        self, qgis_app, base_url: str,
        featureserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        document = _layer_document(base_url, SERVICE_ID, LAYER_ID)
        published = {
            field["name"]: field["domain"]
            for field in document["fields"] if field.get("domain")
        }
        assert "status" in published, (
            "the seed's coded-value domain on test_service/0.status is not in "
            f"the layer document; published domains: {sorted(published)}"
        )
        expected_codes = {
            str(value["code"]): str(value["name"])
            for value in published["status"]["codedValues"]
        }
        assert expected_codes, "the published domain carries no coded values"

        response = httpx.get(
            f"{base_url}/rest/services/{SERVICE_ID}/FeatureServer/queryDomains",
            params={"f": "json", "layers": LAYER_ID}, timeout=30)
        response.raise_for_status()
        answered = [
            domain for domain in response.json().get("domains", [])
            if domain.get("fieldName") == "status"
        ]
        assert answered, "queryDomains does not answer the status domain"

        layer = self._layer(base_url)
        assert layer.isValid(), (
            f"the layer did not load, so no domain can reach a client: "
            f"{layer.error().summary()}"
        )
        index = layer.fields().indexOf("status")
        assert index >= 0, "the provider exposes no 'status' field"
        setup = layer.editorWidgetSetup(index)
        assert setup.type() == "ValueMap", (
            "the provider did not translate the coded-value domain into a "
            f"ValueMap widget; it configured {setup.type()!r}"
        )
        # QGIS stores the ValueMap as [{name: code}, ...] under "map".
        offered = {}
        for entry in setup.config().get("map", []):
            if isinstance(entry, dict):
                for name, code in entry.items():
                    offered[str(code)] = str(name)
        assert offered == expected_codes, (
            "the codes QGIS offers differ from the published domain: "
            f"offered {offered}, published {expected_codes}"
        )
        featureserver_evidence.record(
            "CERT-SCHM-02", "pass", measured_count=len(offered),
            notes=(
                f"coded-value domain StatusDomain on {SERVICE_ID}/{LAYER_ID}.status "
                f"reached QGIS as a ValueMap widget offering exactly the "
                f"{len(offered)} published codes; queryDomains answers the same "
                "domain."
            ),
        )

    # ------------------------------------------------------------------
    # applyEdits / CERT-AUTH-01
    # ------------------------------------------------------------------
    #
    # QGIS builds the attribute object from the layer's full field list, so an
    # unset OID is serialised as "objectid": null and cannot be omitted. The
    # server used to answer {"code":1006,"description":"Field 'objectid'
    # cannot be null."} and drop the edit, so no stock QGIS digitizing session
    # could edit a FeatureServer at all. A null on a server-assigned field on
    # insert now means "assign one", and this case proves the QGIS insert
    # lands, with the omitted-objectid form as the known-good sibling so a
    # failure is attributed to the null OID and not to auth or the endpoint.
    def test_applyedits_insert_from_qgis_lands_on_the_server(
        self, qgis_app, base_url: str,
        featureserver_evidence: CertificationEvidenceCollector,
    ) -> None:
        from qgis.core import (
            QgsApplication,
            QgsAuthMethodConfig,
            QgsFeature,
            QgsGeometry,
            QgsPointXY,
        )

        password = os.environ.get("HONUA_ADMIN_PASSWORD")
        if not password:
            reason = (
                "the FeatureServer edit endpoints require an Esri token (HTTP "
                "Basic is refused with 499), and HONUA_ADMIN_PASSWORD is not "
                "present in this container's environment, so no edit can be "
                "attempted. The pyqgis lane runner must pass it through."
            )
            featureserver_evidence.record("CERT-AUTH-01", "skip", notes=reason)
            pytest.skip(reason)

        edit_endpoint = (
            f"{base_url}/rest/services/{SERVICE_ID}/FeatureServer/{EDIT_LAYER_ID}")

        # A token bound to the request IP. An unbound token - generateToken with
        # no client parameter - is minted happily and then rejected 498 by every
        # service, so the binding is not optional.
        token_response = httpx.post(
            f"{base_url}/sharing/rest/generateToken",
            data={"f": "json", "username": "admin", "password": password,
                  "client": "requestip"},
            timeout=30)
        token_response.raise_for_status()
        token = token_response.json().get("token")
        assert token, "generateToken returned no token"
        auth_header = {"X-Esri-Authorization": f"Bearer {token}"}

        def rows() -> list[dict]:
            response = httpx.get(
                f"{edit_endpoint}/query",
                params={"f": "json", "where": "1=1", "outFields": "*",
                        "returnGeometry": "false"}, timeout=30)
            response.raise_for_status()
            return response.json().get("features", [])

        # Only this module's own markers are removed. The scratch layer is
        # shared with the WFS-T lane, so purging everything could clobber a
        # concurrently running lane's fixture.
        markers = {"cert-omitted-oid", "cert-qgis-insert"}

        def purge() -> None:
            object_ids = [
                row["attributes"]["objectid"] for row in rows()
                if row["attributes"].get("name") in markers
            ]
            if object_ids:
                httpx.post(
                    f"{edit_endpoint}/deleteFeatures", headers=auth_header,
                    data={"f": "json",
                          "objectIds": ",".join(str(i) for i in object_ids)},
                    timeout=30)

        purge()
        try:
            # Known-good sibling: the edit endpoint, the token and the scratch
            # layer all work when the objectid member is simply omitted.
            omitted = httpx.post(
                f"{edit_endpoint}/addFeatures", headers=auth_header,
                data={"f": "json", "features":
                      '[{"attributes":{"name":"cert-omitted-oid"},'
                      '"geometry":{"x":-122.42,"y":37.77}}]'},
                timeout=30)
            omitted.raise_for_status()
            omitted_result = omitted.json()
            assert omitted_result.get("success") is True, (
                "an insert with the objectid omitted was rejected, so the edit "
                "endpoint or the token is at fault rather than the QGIS insert: "
                f"{omitted_result}"
            )
            purge()

            # The provider builds an authenticated layer and advertises editing.
            manager = QgsApplication.authManager()
            if not manager.masterPasswordIsSet():
                manager.setMasterPassword("honua-cert", True)
            config = QgsAuthMethodConfig()
            config.setMethod("EsriToken")
            config.setName("honua-cert-featureserver")
            config.setConfig("token", token)
            manager.storeAuthenticationConfig(config)

            layer = make_featureserver_layer(
                base_url, SERVICE_ID, EDIT_LAYER_ID, authcfg=config.id())
            assert layer.isValid(), (
                "the authenticated layer did not build, so no edit can be "
                f"attributed: {layer.error().summary()}"
            )
            provider = layer.dataProvider()
            from qgis.core import QgsVectorDataProvider

            assert int(provider.capabilities()) & int(
                QgsVectorDataProvider.AddFeatures), (
                "the provider does not advertise AddFeatures: "
                f"{provider.capabilitiesString()}"
            )

            # The insert QGIS actually sends: every field present, objectid null.
            feature = QgsFeature(layer.fields())
            feature.setAttribute("name", "cert-qgis-insert")
            feature.setGeometry(QgsGeometry.fromPointXY(QgsPointXY(-122.42, 37.77)))
            added, _ = provider.addFeatures([feature])
            assert added, (
                "the provider reported the insert failed: "
                f"{provider.lastError() or provider.errors()}"
            )

            landed = [
                row for row in rows()
                if row["attributes"].get("name") == "cert-qgis-insert"
            ]
            assert len(landed) == 1, (
                "the QGIS insert did not land on the server (independent read "
                f"found {len(landed)} rows named cert-qgis-insert)"
            )
            assigned = landed[0]["attributes"].get("objectid")
            assert isinstance(assigned, int) and assigned > 0, (
                f"the server did not assign a real object id: {assigned!r}"
            )
            featureserver_evidence.record(
                "CERT-AUTH-01", "pass", measured_count=1,
                notes=(
                    "applyEdits insert from the QGIS arcgisfeatureserver provider "
                    f"landed and was assigned objectid {assigned}, confirmed by an "
                    "independent query; the request carried the explicit "
                    "\"objectid\": null QGIS always sends on insert. Authenticated "
                    "with an EsriToken authcfg; the omitted-objectid sibling passed "
                    "as the control."
                ),
            )
        finally:
            purge()

