# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
GeoServices REST compatibility checks for non-FeatureServer server types.

These tests intentionally reuse the existing Python integration fixtures so we
exercise the same Honua + PostGIS bootstrap path used by the rest of the
cross-language suites.
"""

from __future__ import annotations

import json

import httpx
import pytest


class TestGeometryServer:
    @pytest.mark.integration
    def test_service_info_returns_descriptor(self, http_client: httpx.Client):
        response = http_client.get("/rest/services/Utilities/Geometry/GeometryServer")

        assert response.status_code == 200

        data = response.json()
        assert data["currentVersion"] > 0
        assert "serviceDescription" in data
        assert data["maxBufferCount"] > 0
        assert data["maxSimplifyCount"] > 0

    @pytest.mark.integration
    def test_project_accepts_json_spatial_reference_objects(
        self, http_client: httpx.Client
    ):
        response = http_client.post(
            "/rest/services/Utilities/Geometry/GeometryServer/project",
            json={
                "geometries": {
                    "geometryType": "esriGeometryPoint",
                    "geometries": [{"x": 0, "y": 0}],
                },
                "inSR": {"latestWkid": 4326},
                "outSR": {"name": "EPSG:3857"},
            },
        )

        assert response.status_code == 200

        data = response.json()
        assert data["geometryType"] == "esriGeometryPoint"
        assert len(data["geometries"]) == 1
        assert data["geometries"][0]["x"] == pytest.approx(0.0, abs=1.0)
        assert data["geometries"][0]["y"] == pytest.approx(0.0, abs=1.0)


class TestImageServer:
    @pytest.mark.integration
    def test_service_info_rejects_unsupported_format(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        response = http_client.get(
            f"/rest/services/{test_layer_id}/ImageServer",
            params={"f": "xml"},
        )

        assert response.status_code == 400

    @pytest.mark.integration
    def test_compute_statistics_histograms_valid_request_returns_statistics(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        response = http_client.get(
            f"/rest/services/{test_layer_id}/ImageServer/computeStatisticsHistograms",
            params={
                "f": "json",
                "geometryType": "esriGeometryEnvelope",
                "geometry": json.dumps(
                    {
                        "xmin": -180,
                        "ymin": -90,
                        "xmax": 180,
                        "ymax": 90,
                    }
                ),
            },
        )

        # computeStatisticsHistograms is implemented and returns per-band statistics and
        # histograms computed from the registered raster for the requested geometry.
        assert response.status_code == 200, response.text
        data = response.json()
        assert "statistics" in data
        assert "histograms" in data
        assert isinstance(data["statistics"], list)
        assert isinstance(data["histograms"], list)

    @pytest.mark.integration
    def test_compute_class_statistics_valid_request_returns_not_implemented(
        self, http_client: httpx.Client, test_layer_id: int
    ):
        response = http_client.get(
            f"/rest/services/{test_layer_id}/ImageServer/computeClassStatistics",
            params={
                "f": "json",
                "classDescriptions": json.dumps(
                    {"classes": [{"value": 1, "name": "class-1"}]}
                ),
            },
        )

        assert response.status_code == 501
