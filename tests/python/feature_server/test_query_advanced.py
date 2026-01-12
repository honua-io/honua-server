# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for advanced GeoServices REST query parameters.

Covers additional query parameters:
- orderByFields (sorting)
- outSR (output spatial reference)
- returnCentroid
- returnExtentOnly
- f=pbf (Protocol Buffers format)
"""

import json

import pytest
import httpx


class TestQueryOrderBy:
    """Tests for orderByFields parameter."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_order_by_asc(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Order results ascending."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "orderByFields": "count ASC",
                "f": "json",
            },
        )
        assert response.status_code == 200

        data = response.json()
        features = data.get("features", [])
        if len(features) >= 2:
            # Verify ordering (if count field exists)
            ids = [
                f.get("attributes", {}).get("count") or f.get("attributes", {}).get("OBJECTID")
                for f in features
            ]
            ids = [i for i in ids if i is not None]
            if len(ids) >= 2:
                assert ids == sorted(ids), "Results should be in ascending order"

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_order_by_desc(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Order results descending."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "orderByFields": "count DESC",
                "f": "json",
            },
        )
        assert response.status_code == 200

        data = response.json()
        features = data.get("features", [])
        if len(features) >= 2:
            ids = [
                f.get("attributes", {}).get("count") or f.get("attributes", {}).get("OBJECTID")
                for f in features
            ]
            ids = [i for i in ids if i is not None]
            if len(ids) >= 2:
                assert ids == sorted(ids, reverse=True), "Results should be in descending order"

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_order_by_multiple_fields(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Order by multiple fields."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "orderByFields": "name ASC, count DESC",
                "f": "json",
            },
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_order_by_invalid_field(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Order by invalid field should return error or ignore."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "orderByFields": "nonexistent_field_xyz ASC",
                "f": "json",
            },
        )
        # May return 400 or succeed ignoring invalid field
        assert response.status_code in [200, 400]


class TestQuerySpatialReference:
    """Tests for outSR (output spatial reference) parameter."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_out_sr_4326(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with outSR=4326 (WGS84)."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "returnGeometry": "true",
                "outSR": "4326",
                "f": "json",
            },
        )
        assert response.status_code == 200

        data = response.json()
        if "spatialReference" in data:
            sr = data["spatialReference"]
            assert sr.get("wkid") == 4326 or sr.get("latestWkid") == 4326

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_out_sr_3857(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with outSR=3857 (Web Mercator)."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "returnGeometry": "true",
                "outSR": "3857",
                "f": "json",
            },
        )
        assert response.status_code == 200

        data = response.json()
        features = data.get("features", [])
        # If features have geometry, coordinates should be in Web Mercator range
        for feature in features:
            geom = feature.get("geometry")
            if geom and "x" in geom:
                # Web Mercator x coordinates are typically large numbers
                # WGS84 would be -180 to 180
                pass  # Just verify the request succeeded

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_out_sr_wkt(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with outSR as WKT string."""
        wkt = 'GEOGCS["WGS 84",DATUM["WGS_1984",SPHEROID["WGS 84",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["degree",0.0174532925199433]]'
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "returnGeometry": "true",
                "outSR": wkt,
                "f": "json",
            },
        )
        # May or may not support WKT
        assert response.status_code in [200, 400]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_out_sr_invalid(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Invalid outSR should return error."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "outSR": "invalid_sr",
                "f": "json",
            },
        )
        assert response.status_code in [200, 400]  # May ignore or error


class TestQuerySpecialOutputs:
    """Tests for special output parameters."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_return_centroid(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with returnCentroid=true."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "returnGeometry": "true",
                "returnCentroid": "true",
                "f": "json",
            },
        )
        assert response.status_code in [200, 400]

        if response.status_code == 200:
            data = response.json()
            features = data.get("features", [])
            for feature in features:
                # May have centroid property
                if "centroid" in feature:
                    centroid = feature["centroid"]
                    assert "x" in centroid and "y" in centroid
        else:
            data = response.json()
            error = data.get("error", {})
            assert "returnCentroid" in " ".join(error.get("details", []))

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_return_extent_only(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with returnExtentOnly=true."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "returnExtentOnly": "true",
                "f": "json",
            },
        )
        assert response.status_code == 200

        data = response.json()
        # Should return extent, not features
        if "extent" in data:
            extent = data["extent"]
            assert "xmin" in extent
            assert "ymin" in extent
            assert "xmax" in extent
            assert "ymax" in extent

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_return_count_only(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with returnCountOnly=true."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "returnCountOnly": "true",
                "f": "json",
            },
        )
        assert response.status_code == 200

        data = response.json()
        # Should return count, not features
        assert "count" in data
        assert isinstance(data["count"], int)

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_return_ids_only(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with returnIdsOnly=true."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "returnIdsOnly": "true",
                "f": "json",
            },
        )
        assert response.status_code == 200

        data = response.json()
        # Should return object IDs, not full features
        assert "objectIds" in data or "objectIdFieldName" in data


class TestQueryFormats:
    """Tests for query output formats."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_format_json(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with f=json."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "f": "json"},
        )
        assert response.status_code == 200
        content_type = response.headers.get("content-type", "")
        assert "json" in content_type.lower()

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_format_geojson(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with f=geojson."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "f": "geojson"},
        )
        assert response.status_code == 200
        data = response.json()
        assert data.get("type") == "FeatureCollection"

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_format_pbf(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with f=pbf (Protocol Buffers)."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "f": "pbf"},
        )
        # PBF may or may not be supported
        assert response.status_code in [200, 400, 501]

        if response.status_code == 200:
            content_type = response.headers.get("content-type", "")
            # Should be protobuf or octet-stream
            assert any(ct in content_type for ct in [
                "application/x-protobuf",
                "application/octet-stream",
                "application/protobuf",
            ])
            # Content should be binary
            assert isinstance(response.content, bytes)

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_format_pjson(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with f=pjson (pretty JSON)."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "f": "pjson"},
        )
        # pjson may map to json or be unsupported
        assert response.status_code in [200, 400]


class TestQueryDistinct:
    """Tests for distinct/unique value queries."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_return_distinct_values(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with returnDistinctValues=true."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "outFields": "name",
                "returnDistinctValues": "true",
                "f": "json",
            },
        )
        assert response.status_code == 200


class TestQueryStatistics:
    """Tests for statistics queries."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_out_statistics(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with outStatistics parameter."""
        statistics = [
            {"statisticType": "count", "onStatisticField": "count", "outStatisticFieldName": "total_count"}
        ]
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "outStatistics": json.dumps(statistics),
                "f": "json",
            },
        )
        assert response.status_code in [200, 400]

        if response.status_code == 200:
            data = response.json()
            # Should return statistics, not features
            features = data.get("features", [])
            if features:
                # Statistics results in attributes
                assert "attributes" in features[0]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_group_by_statistics(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with grouped statistics."""
        statistics = [
            {"statisticType": "count", "onStatisticField": "count", "outStatisticFieldName": "count"}
        ]
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "outStatistics": json.dumps(statistics),
                "groupByFieldsForStatistics": "name",
                "f": "json",
            },
        )
        assert response.status_code in [200, 400]
