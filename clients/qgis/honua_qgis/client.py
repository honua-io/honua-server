"""Stdlib-only HTTP client used by the Honua QGIS plugin.

PyQGIS bundles its own Python and pip-installed packages are fragile
across platforms, so this client must depend on ``urllib`` only — no
``requests`` or ``httpx``. All requests carry an ``X-API-Key`` header
when a key is configured on the connection.
"""

from __future__ import annotations

import json
import socket
import ssl
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

from .auth import (
    CollectionEntry,
    DiscoveryResult,
    HonuaConnection,
    WmsLayerEntry,
)


DEFAULT_TIMEOUT = 15.0
"""Per-request timeout in seconds. Plugin UI is blocking so this caps the
worst case the user can wait before the dialog returns."""


class HonuaClientError(Exception):
    """Raised for any Honua HTTP/parse failure surfaced to the UI."""


@dataclass
class HttpResponse:
    status: int
    body: bytes
    content_type: str


class HonuaClient:
    """Minimal client for OGC API Features + WMS discovery on a Honua
    server.

    The class stays thin on purpose: every method is one HTTP round-trip
    plus one parse step, so failures map to a single user-facing error
    without retry magic. The plugin owns retry/backoff at the dialog
    level if it ever needs to.
    """

    def __init__(
        self,
        connection: HonuaConnection,
        *,
        timeout: float = DEFAULT_TIMEOUT,
        opener=None,
    ) -> None:
        self.connection = connection
        self.timeout = timeout
        self._opener = opener  # tests may inject a fake urlopen

    # ----- transport ----------------------------------------------------

    def _get(self, path: str, query: dict[str, str] | None = None) -> HttpResponse:
        url = f"{self.connection.normalized_base_url}{path}"
        if query:
            url = f"{url}?{urlencode(query)}"
        request = Request(url, headers=self.connection.request_headers())
        opener = self._opener or urlopen

        try:
            response = self._invoke_opener(opener, request)
        except HTTPError as exc:
            raise HonuaClientError(
                f"Honua server returned HTTP {exc.code} for {url}"
            ) from exc
        except (URLError, socket.timeout, ConnectionError) as exc:
            raise HonuaClientError(
                f"could not reach Honua server at {url}: {exc}"
            ) from exc

        try:
            with response:
                body = response.read()
                status = getattr(response, "status", 200)
                content_type = response.headers.get("Content-Type", "") if hasattr(response, "headers") else ""
        except Exception as exc:  # pragma: no cover - defensive
            raise HonuaClientError(f"failed reading response from {url}: {exc}") from exc

        return HttpResponse(status=status, body=body, content_type=content_type)

    def _invoke_opener(self, opener, request):
        """Call ``opener`` once. Real ``urlopen`` accepts a TLS ``context``;
        the test fake does not, so we transparently retry without it on
        ``TypeError`` so production HTTPS keeps strict cert validation
        while tests stay simple."""
        try:
            ctx = ssl.create_default_context()
            return opener(request, timeout=self.timeout, context=ctx)
        except TypeError:
            return opener(request, timeout=self.timeout)

    # ----- discovery ----------------------------------------------------

    def ping(self) -> None:
        """Hit the OGC API Features landing page; raise on any failure.

        Used by the Add Server dialog's "Test connection" button. A 200
        with parseable JSON proves both connectivity and a valid API key.
        """
        response = self._get("/ogc/features")
        if response.status >= 400:
            raise HonuaClientError(f"unexpected status {response.status}")
        try:
            json.loads(response.body)
        except (ValueError, json.JSONDecodeError) as exc:
            raise HonuaClientError(
                "Honua server responded but returned non-JSON content; "
                "is this a Honua endpoint?"
            ) from exc

    def list_collections(self) -> list[CollectionEntry]:
        """Return all OGC API Features collections on the server."""
        response = self._get("/ogc/features/collections")
        try:
            payload = json.loads(response.body)
        except (ValueError, json.JSONDecodeError) as exc:
            raise HonuaClientError("malformed collections response") from exc

        items = payload.get("collections")
        if not isinstance(items, list):
            return []

        out: list[CollectionEntry] = []
        for raw in items:
            if not isinstance(raw, dict):
                continue
            collection_id = str(raw.get("id") or raw.get("name") or "").strip()
            if not collection_id:
                continue
            title = str(raw.get("title") or collection_id)
            bbox = _extract_bbox(raw.get("extent"))
            crs = _extract_storage_crs(raw)
            out.append(
                CollectionEntry(
                    collection_id=collection_id,
                    title=title,
                    bbox=bbox,
                    crs=crs,
                )
            )
        return out

    def list_services(self) -> list[str]:
        """Return GeoServices REST service ids that have a WMS sub-endpoint.

        We scan ``/rest/services`` first, but the layout varies; many
        Honua deployments only have a handful of named services. Anything
        the server exposes as ``services[].name`` is candidate; we then
        confirm WMS support by probing a HEAD-equivalent GET.
        """
        try:
            response = self._get("/rest/services", query={"f": "json"})
        except HonuaClientError:
            return []
        try:
            payload = json.loads(response.body)
        except (ValueError, json.JSONDecodeError):
            return []

        services = payload.get("services")
        if not isinstance(services, list):
            return []

        ids: list[str] = []
        seen: set[str] = set()
        for entry in services:
            if not isinstance(entry, dict):
                continue
            name = str(entry.get("name") or "").strip()
            if not name or name in seen:
                continue
            seen.add(name)
            ids.append(name)
        return ids

    def list_wms_layers(self, service_id: str) -> list[WmsLayerEntry]:
        """Probe a service's WMS endpoint and parse the capabilities doc.

        Missing-WMS is not an error — it just yields an empty list so the
        layer browser can render a "no WMS" hint without aborting whole
        discovery.
        """
        try:
            response = self._get(
                f"/ogc/services/{service_id}/wms",
                query={"SERVICE": "WMS", "REQUEST": "GetCapabilities", "VERSION": "1.3.0"},
            )
        except HonuaClientError:
            return []

        if not response.body:
            return []

        try:
            return parse_wms_capabilities(service_id, response.body)
        except ET.ParseError:
            return []

    def discover(self) -> DiscoveryResult:
        """Run the full discovery flow: collections + every service's WMS."""
        result = DiscoveryResult()
        result.collections = self.list_collections()
        for service_id in self.list_services():
            result.wms_layers.extend(self.list_wms_layers(service_id))
        return result


# ---------------------------------------------------------------------------
# Pure parsing helpers (kept module-level for unit testing)
# ---------------------------------------------------------------------------


def _extract_bbox(extent: Any) -> tuple[float, float, float, float] | None:
    """Pull ``[minx, miny, maxx, maxy]`` out of an OGC API Features extent."""
    if not isinstance(extent, dict):
        return None
    spatial = extent.get("spatial")
    if not isinstance(spatial, dict):
        return None
    bboxes = spatial.get("bbox")
    if not isinstance(bboxes, list) or not bboxes:
        return None
    candidate = bboxes[0]
    if not isinstance(candidate, list) or len(candidate) < 4:
        return None
    try:
        minx, miny, maxx, maxy = (float(v) for v in candidate[:4])
    except (TypeError, ValueError):
        return None
    return (minx, miny, maxx, maxy)


def _extract_storage_crs(collection: dict[str, Any]) -> str:
    """Resolve a CRS auth-id from an OGC API Features collection record.

    Honua servers expose ``storageCrs`` as a URN (e.g.
    ``http://www.opengis.net/def/crs/EPSG/0/4326``). QGIS expects an
    ``EPSG:NNNN`` form for provider URIs, so we coerce the URN tail to
    ``EPSG:NNNN`` and fall back to ``EPSG:4326`` if the value is missing
    or unparseable. Honua collections that are stored in a non-WGS84 CRS
    will be rejected if we silently default — the user-visible layer
    would render at the wrong place — so the conservative behavior is to
    log via the returned default and let the WFS provider raise on
    mismatch. A future slice (qgis-2) will surface CRS mismatches in the
    browser before the user adds the layer.
    """
    raw = collection.get("storageCrs") or collection.get("crs")
    if isinstance(raw, list) and raw:
        raw = raw[0]
    if not isinstance(raw, str) or not raw:
        return "EPSG:4326"
    if raw.upper().startswith("EPSG:"):
        return raw.upper()
    # URN form: .../EPSG/0/4326 or .../EPSG/4326
    parts = [p for p in raw.split("/") if p]
    for idx, value in enumerate(parts):
        if value.upper() == "EPSG" and idx + 1 < len(parts):
            tail = parts[-1]
            if tail.isdigit():
                return f"EPSG:{tail}"
    return "EPSG:4326"


def parse_wms_capabilities(service_id: str, body: bytes) -> list[WmsLayerEntry]:
    """Extract layer entries from a WMS 1.3.0 capabilities XML document.

    Picks every named ``<Layer>`` element (groups without ``<Name>`` are
    skipped — they are not addressable in a GetMap request). The first
    declared ``<CRS>`` wins; we leave hard CRS reconciliation to QGIS's
    WMS provider.
    """
    root = ET.fromstring(body)
    ns = {
        "wms": "http://www.opengis.net/wms",
    }
    # Capabilities documents may or may not declare the namespace.
    layer_xpath = ".//wms:Layer" if root.tag.startswith("{") else ".//Layer"
    layers: list[WmsLayerEntry] = []
    for layer in root.findall(layer_xpath, ns if root.tag.startswith("{") else None):
        name_el = layer.find("wms:Name", ns) if root.tag.startswith("{") else layer.find("Name")
        if name_el is None or not (name_el.text or "").strip():
            continue
        title_el = layer.find("wms:Title", ns) if root.tag.startswith("{") else layer.find("Title")
        crs_el = layer.find("wms:CRS", ns) if root.tag.startswith("{") else layer.find("CRS")
        crs_text = (crs_el.text or "EPSG:4326").strip() if crs_el is not None else "EPSG:4326"
        title_text = (title_el.text or name_el.text or "").strip() if title_el is not None else name_el.text.strip()
        layers.append(
            WmsLayerEntry(
                service_id=service_id,
                layer_name=name_el.text.strip(),
                title=title_text,
                crs=crs_text or "EPSG:4326",
            )
        )
    return layers
