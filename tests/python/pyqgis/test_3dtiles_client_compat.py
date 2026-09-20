# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
OGC 3D Tiles read compatibility through the real QGIS ``cesiumtiles`` provider.

The tileset under test is the one the client-compat fixture publishes from the
``browser_compat`` polygon layer (``docker/client-compat/seed/publish-scene.py``,
scene id ``cert-browser-polygons``) at ``/scenes/{sceneId}/tileset.json`` with one
glTF-binary content tile beside it. Nothing here calls the admin surface: an
unpublished scene fails these cases with a 404, which is the honest report.

Two client-side facts are load-bearing:

1. QGIS 3.44 reads 3D Tiles through ``QgsTiledSceneLayer`` with the
   ``cesiumtiles`` provider, addressed as ``url=<tileset.json>``. The provider is
   present in the lane's build (``QgsProviderRegistry`` lists ``cesiumtiles``
   beside ``quantizedmesh`` and ``tiledscene``), so this is a native read, not
   an adapted one.
2. The provider's index is the only client path to tile content. ``getTiles``
   returns tile *ids* and ``getTile(id).resources()["content"]`` the resolved
   content URL, which ``retrieveContent`` fetches through QGIS's own network
   stack. Reading the .glb with urllib would prove the server, not the client,
   so the content case goes through the index.
"""

from __future__ import annotations

import json
import urllib.request

import pytest

from .conftest import CertificationEvidenceCollector

SCENE_ID = "cert-browser-polygons"
SOURCE_SERVICE_ID = "browser_compat"
SOURCE_LAYER_ID = 2002

GLTF_MAGIC = b"glTF"

# The generator centres each tile on the source geometry; the scene's bounding
# volume therefore lies inside the source layer's advertised extent. The pad
# absorbs the region-to-degrees rounding in the tileset (a few metres), not a
# wrong CRS or a swapped axis, either of which is off by whole degrees.
EXTENT_PAD_DEG = 0.01


def _tileset_url(base_url: str) -> str:
    return f"{base_url.rstrip('/')}/scenes/{SCENE_ID}/tileset.json"


def _layer(base_url: str):
    from qgis.core import QgsTiledSceneLayer

    return QgsTiledSceneLayer(f"url={_tileset_url(base_url)}", "3d-tiles-cert", "cesiumtiles")


def _source_extent(base_url: str) -> dict[str, float]:
    url = f"{base_url.rstrip('/')}/rest/services/{SOURCE_SERVICE_ID}/FeatureServer/{SOURCE_LAYER_ID}?f=json"
    with urllib.request.urlopen(url, timeout=30) as response:
        document = json.load(response)
    extent = document["extent"]
    assert extent["spatialReference"]["wkid"] == 4326, extent
    return extent


@pytest.mark.pyqgis
class Test3DTilesClientCompat:
    """3D Tiles 1.1 tileset read via the QGIS cesiumtiles provider."""

    # NB-PQG-3DT-01 / tileset
    def test_tileset_loads_as_a_tiled_scene_layer(
        self, qgis_app, base_url: str,
        tiles3d_evidence: CertificationEvidenceCollector,
    ) -> None:
        layer = _layer(base_url)
        assert layer.isValid(), (
            f"cesiumtiles did not load {_tileset_url(base_url)}: "
            f"{layer.error().summary() or 'no error reported'}"
        )
        provider = layer.dataProvider()
        assert provider.name() == "cesiumtiles"
        assert provider.sceneCrs().authid() == "EPSG:4978", (
            "3D Tiles content is earth-centred; the provider reported "
            f"{provider.sceneCrs().authid()}"
        )

        # The layer extent is reported geographically (EPSG:4979) and must sit
        # inside the source polygon layer's advertised extent.
        extent = layer.extent()
        source = _source_extent(base_url)
        assert layer.crs().authid() == "EPSG:4979", layer.crs().authid()
        assert extent.xMinimum() >= source["xmin"] - EXTENT_PAD_DEG
        assert extent.xMaximum() <= source["xmax"] + EXTENT_PAD_DEG
        assert extent.yMinimum() >= source["ymin"] - EXTENT_PAD_DEG
        assert extent.yMaximum() <= source["ymax"] + EXTENT_PAD_DEG, (
            f"scene extent {extent.toString(4)} is not within the source layer "
            f"extent {source}"
        )
        assert extent.width() > 0 and extent.height() > 0, extent.toString(4)

        tiles3d_evidence.record(
            "NB-PQG-3DT-01", "pass", measured_count=1,
            notes=(
                f"QgsTiledSceneLayer loaded /scenes/{SCENE_ID}/tileset.json through "
                "the cesiumtiles provider: scene CRS EPSG:4978, layer extent "
                f"{extent.toString(4)} (EPSG:4979) inside the source layer "
                f"{SOURCE_SERVICE_ID}/{SOURCE_LAYER_ID} extent."
            ),
        )

    # NB-PQG-3DT-02 / tileset (content)
    def test_tile_content_is_fetched_through_the_provider_index(
        self, qgis_app, base_url: str,
        tiles3d_evidence: CertificationEvidenceCollector,
    ) -> None:
        from qgis.core import QgsTiledSceneRequest

        layer = _layer(base_url)
        assert layer.isValid(), layer.error().summary()
        index = layer.dataProvider().index()
        assert index.isValid(), "the provider built no tile index from the tileset"

        request = QgsTiledSceneRequest()
        request.setRequiredGeometricError(0)
        tiles = [index.getTile(tile_id) for tile_id in index.getTiles(request)]
        assert tiles, "the index returned no tiles for a full-detail request"

        content_urls = [
            tile.resources()["content"] for tile in tiles if "content" in tile.resources()
        ]
        assert content_urls, (
            "no tile carries content; the index saw only the root: "
            f"{[tile.resources() for tile in tiles]}"
        )
        origin = _tileset_url(base_url).rsplit("/", 1)[0]
        for url in content_urls:
            assert url.startswith(origin + "/"), (
                f"content {url!r} was resolved outside the tileset's own location "
                f"{origin!r}: the tileset advertised a foreign or absolute uri"
            )

        payloads = [bytes(index.retrieveContent(url)) for url in content_urls]
        for url, payload in zip(content_urls, payloads):
            assert payload[:4] == GLTF_MAGIC, (
                f"{url} did not come back as glTF binary through the provider "
                f"(first bytes {payload[:8]!r}, {len(payload)} bytes)"
            )

        tiles3d_evidence.record(
            "NB-PQG-3DT-02", "pass", measured_count=len(payloads),
            notes=(
                f"{len(payloads)} content tile(s) enumerated from the cesiumtiles "
                "index at geometric error 0 and fetched through retrieveContent; "
                f"each is glTF binary ({[len(p) for p in payloads]} bytes), "
                "addressed under the tileset's own location."
            ),
        )
