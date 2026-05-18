"""Build QGIS provider URIs for Honua collections and WMS layers.

These functions are pure string builders — they do not import PyQGIS — so
they can be unit-tested on a vanilla Python interpreter without QGIS
installed. The plugin's dock widget calls them and feeds the result to
``QgsVectorLayer`` / ``QgsRasterLayer``.
"""

from __future__ import annotations

from urllib.parse import quote

from .auth import (
    CollectionEntry,
    HonuaConnection,
    WmsLayerEntry,
    encode_api_key_query,
)


_WMS_PROVIDER_VALUE_SAFE = ":/?=%"
"""Characters safe inside a QGIS ``wms`` provider key/value field.

``&`` is intentionally omitted because the provider URI itself uses
ampersands to separate fields.
"""


def build_wfs_uri(connection: HonuaConnection, collection: CollectionEntry) -> str:
    """Build the QGIS ``WFS`` provider URI for an OGC API Features collection.

    QGIS 3.22's WFS provider supports OGC API Features when the URI carries
    ``version=auto`` and the URL points at the collection items endpoint.
    The API key is passed in via the ``X-API-Key``-equivalent ``apikey``
    query parameter on the items URL itself (see ``encode_api_key_query``
    for the rationale).
    """
    base = connection.normalized_base_url
    items_url = f"{base}/ogc/features/collections/{quote(collection.collection_id, safe='')}/items"
    api_key_qs = encode_api_key_query(connection.api_key)
    if api_key_qs:
        items_url = f"{items_url}?{api_key_qs}"

    parts = [
        f"url={quote(items_url, safe='')}",
        f"typename={quote(collection.collection_id, safe='')}",
        "version=auto",
        f"srsname={quote(collection.crs, safe='')}",
        "restrictToRequestBBOX=1",
        "pagingEnabled=true",
        "preferCoordinatesForWfsT11=false",
    ]
    return " ".join(parts)


def build_wms_uri(connection: HonuaConnection, layer: WmsLayerEntry) -> str:
    """Build the QGIS ``wms`` provider URI for a discovered WMS layer.

    QGIS uses an ampersand-joined key=value list (URL-encoded) — *not* a
    real URL — for the ``wms`` provider URI. The endpoint URL itself goes
    in the ``url`` field; the API key rides as a query parameter on that
    URL because QGIS's WMS provider does not honour custom request
    headers from the application network manager.
    """
    base = connection.normalized_base_url
    endpoint = f"{base}/ogc/services/{quote(layer.service_id, safe='')}/wms"
    api_key_qs = encode_api_key_query(connection.api_key)
    if api_key_qs:
        endpoint = f"{endpoint}?{api_key_qs}"

    params = {
        "url": endpoint,
        "format": "image/png",
        "layers": layer.layer_name,
        "styles": "",
        "crs": layer.crs,
        "version": "1.3.0",
        "tileMatrixSet": "",
    }
    return "&".join(
        f"{key}={quote(str(value), safe=_WMS_PROVIDER_VALUE_SAFE)}"
        for key, value in params.items()
    )


def display_label(*, kind: str, title: str, identifier: str) -> str:
    """Stable label used both in the layer browser tree and the project legend."""
    if title and title != identifier:
        return f"{title} ({identifier})"
    return identifier
