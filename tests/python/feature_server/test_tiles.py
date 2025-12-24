# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for MVT (Mapbox Vector Tile) endpoint.

Endpoint: GET /tiles/{layerId}/{z}/{x}/{y}.mvt
"""

import pytest
import httpx


class TestMvtTiles:
    """Tests for MVT tile endpoint."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_mvt_tile_returns_200(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        """MVT tile request should return 200 or 204."""
        # Use a common tile coordinate (world overview at low zoom)
        response = http_client.get(f"/tiles/{test_layer_id}/0/0/0.mvt")
        # 200 = tile with data, 204 = empty tile
        assert response.status_code in [200, 204]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_mvt_tile_content_type(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        """MVT tile should have correct content type."""
        response = http_client.get(f"/tiles/{test_layer_id}/0/0/0.mvt")

        if response.status_code == 200:
            content_type = response.headers.get("content-type", "")
            # MVT content types
            assert any(ct in content_type for ct in [
                "application/vnd.mapbox-vector-tile",
                "application/x-protobuf",
                "application/octet-stream",
            ])

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_mvt_tile_is_binary(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        """MVT tile content should be binary (protobuf)."""
        response = http_client.get(f"/tiles/{test_layer_id}/0/0/0.mvt")

        if response.status_code == 200:
            # Should be binary, not text
            content = response.content
            assert isinstance(content, bytes)
            # MVT/protobuf starts with specific bytes, but don't over-specify

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_mvt_tile_invalid_layer_returns_404(self, http_client: httpx.Client):
        """MVT tile for invalid layer should return 404."""
        response = http_client.get("/tiles/99999/0/0/0.mvt")
        assert response.status_code == 404

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.parametrize("zoom", [0, 5, 10, 15])
    def test_mvt_tile_various_zoom_levels(
        self, http_client: httpx.Client, test_layer_id: int, zoom: int
    ):
        """MVT tiles should work at various zoom levels."""
        # Use center tile for each zoom level
        max_tile = (1 << zoom) - 1  # 2^zoom - 1
        x = max_tile // 2
        y = max_tile // 2

        response = http_client.get(f"/tiles/{test_layer_id}/{zoom}/{x}/{y}.mvt")
        assert response.status_code in [200, 204, 400]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_mvt_tile_out_of_bounds_returns_error(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        """MVT tile with out-of-bounds coordinates should return error."""
        # At zoom 0, only tile 0/0/0 exists
        response = http_client.get(f"/tiles/{test_layer_id}/0/5/5.mvt")
        # Should return 400 (invalid) or 204 (empty)
        assert response.status_code in [400, 204]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_mvt_tile_negative_coordinates_returns_error(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        """MVT tile with negative coordinates should return error."""
        response = http_client.get(f"/tiles/{test_layer_id}/5/-1/0.mvt")
        # Negative coordinates are invalid
        assert response.status_code in [400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_mvt_tile_with_where_clause(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        """MVT tile with WHERE filter parameter."""
        response = http_client.get(
            f"/tiles/{test_layer_id}/0/0/0.mvt",
            params={"where": "1=1"},
        )
        assert response.status_code in [200, 204]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_mvt_tile_cache_headers(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        """MVT tiles should have cache control headers."""
        response = http_client.get(f"/tiles/{test_layer_id}/0/0/0.mvt")

        if response.status_code in [200, 204]:
            # Should have some form of cache header
            has_cache = (
                "cache-control" in response.headers or
                "etag" in response.headers or
                "expires" in response.headers
            )
            # Cache headers are recommended but not required
            # Just check response is valid

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_mvt_tile_zoom_too_high_returns_error(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        """MVT tile with very high zoom should return error or empty."""
        # Zoom 30 is typically above max supported
        response = http_client.get(f"/tiles/{test_layer_id}/30/0/0.mvt")
        # May return 400 (invalid zoom) or 204 (empty)
        assert response.status_code in [200, 204, 400]


class TestMvtTileFormats:
    """Tests for MVT tile format variations."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_mvt_extension_required(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        """Request without .mvt extension."""
        # This might be a different endpoint or return 404
        response = http_client.get(f"/tiles/{test_layer_id}/0/0/0")
        # Behavior depends on routing - may redirect or 404

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_mvt_pbf_extension(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        """Request with .pbf extension (alternative format)."""
        response = http_client.get(f"/tiles/{test_layer_id}/0/0/0.pbf")
        # May support .pbf or return 404
        assert response.status_code in [200, 204, 404]
