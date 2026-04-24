# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""GPServer smoke tests for the current adapter surface."""

from __future__ import annotations

import httpx
import pytest

POINT_WKB_BASE64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA"


class TestGPServerSmoke:
    """Quick checks for the GPServer REST surface."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_gpserver_service_root_returns_catalog_metadata(
        self, http_client: httpx.Client, test_service_id: str
    ):
        response = http_client.get(
            f"/rest/services/{test_service_id}/GPServer",
            params={"f": "json"},
        )

        assert response.status_code == 200

        data = response.json()
        assert data["currentVersion"] == 10.81
        assert data["executionType"] == "esriExecutionTypeAsynchronous"
        assert data["capabilities"] == ""
        assert data["resultMapServerName"] == ""
        assert "geometry.buffer" in data["tasks"]
        assert test_service_id in data["serviceDescription"]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_gpserver_task_metadata_returns_catalog_shape(
        self, http_client: httpx.Client, test_service_id: str
    ):
        response = http_client.get(
            f"/rest/services/{test_service_id}/GPServer/geometry.buffer",
            params={"f": "json"},
        )

        assert response.status_code == 200

        data = response.json()
        assert data["name"] == "geometry.buffer"
        assert data["displayName"] == "Buffer"
        assert "Creates a polygon" in data["description"]
        assert data["category"] == "geometry"
        assert data["helpUrl"] == ""
        assert data["executionType"] == "esriExecutionTypeAsynchronous"
        assert any(
            parameter["name"] == "distance"
            and parameter["description"] == "Buffer distance in meters."
            and parameter["direction"] == "esriGPParameterDirectionInput"
            for parameter in data["parameters"]
        )

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_gpserver_submit_job_rejects_unsupported_env_controls(
        self, http_client: httpx.Client, test_service_id: str
    ):
        response = http_client.post(
            f"/rest/services/{test_service_id}/GPServer/geometry.buffer/submitJob",
            data={
                "f": "json",
                "wkb": POINT_WKB_BASE64,
                "srid": "4326",
                "distance": "10",
                "env:outSR": "4326",
            },
        )

        assert response.status_code == 400

        data = response.json()
        assert data["error"]["code"] == 400
        assert data["error"]["message"] == "Bad Request"

        details = " ".join(data["error"].get("details") or [])
        assert "GP environment controls are not yet supported" in details
        assert "env:outSR" in details

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_gpserver_submit_job_accepts_canonical_payload_before_store_check(
        self, http_client: httpx.Client, test_service_id: str
    ):
        response = http_client.post(
            f"/rest/services/{test_service_id}/GPServer/geometry.buffer/submitJob",
            data={
                "f": "json",
                "wkb": POINT_WKB_BASE64,
                "srid": "4326",
                "distance": "10",
            },
        )

        assert response.status_code in (200, 503)

        data = response.json()
        if response.status_code == 200:
            assert data["jobId"]
            assert data["jobStatus"] == "esriJobSubmitted"
            return

        assert data["error"]["code"] == 503
        assert data["error"]["message"] == "Service Unavailable"
        details = " ".join(data["error"].get("details") or [])
        assert "Redis-backed durable storage" in details
