# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Geometry matrix tests for OGC API Features transaction endpoints.
"""

import pytest
import httpx

from shared.geometry import GeometryGenerator


def _extract_feature_id(response: httpx.Response) -> str | None:
    location = response.headers.get("location", "")
    if location:
        return location.rstrip("/").split("/")[-1]
    try:
        data = response.json()
    except ValueError:
        return None
    return data.get("id") if isinstance(data, dict) else None


class TestTransactionsGeometryMatrix:
    """Create/delete matrix across geometry types."""

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.slow
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
    def test_create_delete_feature_geometry(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
        geometry_method: str,
    ):
        geom = getattr(geometry_generator, geometry_method)()
        feature = {
            "type": "Feature",
            "geometry": geom.geojson,
            "properties": {"name": f"txn_{geom.name}"},
        }

        create_response = http_client.post(
            f"/ogc/features/collections/{test_collection_id}/items",
            json=feature,
            headers={"Content-Type": "application/geo+json"},
        )

        if create_response.status_code in [405, 501]:
            pytest.skip("Transactions not supported")

        assert create_response.status_code in [200, 201]
        feature_id = _extract_feature_id(create_response)
        assert feature_id is not None

        delete_response = http_client.delete(
            f"/ogc/features/collections/{test_collection_id}/items/{feature_id}"
        )
        assert delete_response.status_code in [200, 204]
