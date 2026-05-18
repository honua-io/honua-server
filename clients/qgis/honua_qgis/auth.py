"""Connection + auth model for the Honua plugin.

A ``HonuaConnection`` is a single user-saved server entry: name, base URL,
API key. The plugin saves connections through ``QSettings`` (no QGIS
``QgsAuthManager`` dependency in the first slice — that integration is
tracked under qgis-4 once API-key-only ships).

The model deliberately has no PyQt5/PyQGIS imports so the dialog logic and
discovery flow can be unit-tested without QGIS.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Iterable
from urllib.parse import urlparse


SETTINGS_GROUP = "honua/connections"
"""``QSettings`` group key. Each connection is stored under
``honua/connections/<name>/<field>``."""


@dataclass(frozen=True)
class HonuaConnection:
    """Saved connection to a single Honua server.

    ``name`` is the user-facing label and the ``QSettings`` sub-key.
    ``base_url`` is the Honua server root (e.g. ``https://my.honua.io``)
    without a trailing slash. ``api_key`` is sent as ``X-API-Key`` and may
    be empty for anonymous endpoints.
    """

    name: str
    base_url: str
    api_key: str = ""

    def __post_init__(self) -> None:
        if not self.name or not self.name.strip():
            raise ValueError("connection name is required")
        if not self.base_url:
            raise ValueError("base URL is required")
        parsed = urlparse(self.base_url)
        if parsed.scheme not in ("http", "https"):
            raise ValueError("base URL must be http(s)")
        if not parsed.netloc:
            raise ValueError("base URL must include a host")

    @property
    def normalized_base_url(self) -> str:
        """Base URL with any trailing slashes stripped."""
        return self.base_url.rstrip("/")

    def request_headers(self) -> dict[str, str]:
        """Headers that every plugin HTTP call must carry."""
        headers = {
            "Accept": "application/json",
            "User-Agent": "honua-qgis/0.1",
        }
        if self.api_key:
            headers["X-API-Key"] = self.api_key
        return headers


def encode_api_key_query(api_key: str) -> str:
    """Return a ``&apikey=…`` fragment for embedding in QGIS provider URIs.

    QGIS's built-in WFS/WMS providers do not currently honour custom
    request headers set on the application-wide
    ``QgsNetworkAccessManager``. The fallback documented in the design is
    to embed the API key in the provider URI as a query parameter; the
    Honua server accepts ``apikey`` as a query alias for ``X-API-Key``.
    """
    if not api_key:
        return ""
    from urllib.parse import quote

    return f"apikey={quote(api_key, safe='')}"


def filter_unique_connection_names(names: Iterable[str]) -> list[str]:
    """Return ``names`` deduplicated and stripped, preserving order.

    Used by the dialog to keep the saved-connection dropdown stable when
    the underlying ``QSettings`` group has duplicate entries from older
    plugin versions.
    """
    seen: set[str] = set()
    result: list[str] = []
    for raw in names:
        candidate = (raw or "").strip()
        if not candidate or candidate in seen:
            continue
        seen.add(candidate)
        result.append(candidate)
    return result


@dataclass
class CollectionEntry:
    """One vector collection discovered via OGC API Features."""

    collection_id: str
    title: str
    bbox: tuple[float, float, float, float] | None = None
    crs: str = "EPSG:4326"


@dataclass
class WmsLayerEntry:
    """One raster layer discovered via WMS GetCapabilities."""

    service_id: str
    layer_name: str
    title: str
    crs: str = "EPSG:4326"


@dataclass
class DiscoveryResult:
    """All discoverable layers for a connected server."""

    collections: list[CollectionEntry] = field(default_factory=list)
    wms_layers: list[WmsLayerEntry] = field(default_factory=list)

    @property
    def total(self) -> int:
        return len(self.collections) + len(self.wms_layers)
