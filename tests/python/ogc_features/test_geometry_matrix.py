# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Geometry matrix tests for OGC API Features spatial filters.
"""

import pytest
import httpx

from shared.geometry import GeometryGenerator


class TestGeometryMatrix:
    """Spatial filter matrix across geometry types."""

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    @pytest.mark.parametrize(
        "geometry_method",
        [
            "point",
            "multipoint",
            "linestring",
            "multilinestring",
            "polygon_simple",
            "polygon_with_hole",
            "multipolygon_simple",
            "geometry_collection",
        ],
    )
    def test_items_spatial_filter_by_geometry(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
        geometry_method: str,
    ):
        geom = getattr(geometry_generator, geometry_method)()
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": f"S_INTERSECTS(geometry, {geom.wkt})"},
        )
        assert response.status_code == 200
