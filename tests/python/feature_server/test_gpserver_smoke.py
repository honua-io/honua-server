# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""GPServer smoke tests for the current adapter surface."""

from __future__ import annotations

import httpx
import pytest

from shared.geoservices import assert_geoservices_error

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
            and parameter["description"].startswith("Buffer distance in the input geometry")
            and parameter["direction"] == "esriGPParameterDirectionInput"
            for parameter in data["parameters"]
        )

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_gpserver_submit_job_rejects_unsupported_env_controls(
        self, http_client: httpx.Client, test_service_id: str
    ):
        # #2787: env:outSR/env:processSR/env:workspace/env:overwriteOutput are now
        # honored on submitJob, so an unrecognized control (env:extent) exercises
        # the rejection path instead.
        response = http_client.post(
            f"/rest/services/{test_service_id}/GPServer/geometry.buffer/submitJob",
            data={
                "f": "json",
                "wkb": POINT_WKB_BASE64,
                "srid": "4326",
                "distance": "10",
                "env:extent": "-180,-90,180,90",
            },
        )

        # PA-070/PA-117 (#2418): GeoServices REST signals errors with HTTP 200 and an
        # {"error": {"code": N}} body; the error code moved into the body, not the status.
        assert response.status_code == 200

        data = response.json()
        assert data["error"]["code"] == 400
        assert data["error"]["message"] == "Bad Request"

        details = " ".join(data["error"].get("details") or [])
        assert "GP environment controls are not yet supported" in details
        assert "env:extent" in details

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

        # PA-070/PA-117 (#2418): the store-unavailable case now returns HTTP 200 with a
        # {"error": {"code": 503}} body instead of a 503 status; branch on the body.
        assert response.status_code in (200, 503)

        data = response.json()
        if "error" not in data:
            assert data["jobId"]
            assert data["jobStatus"] == "esriJobSubmitted"
            return

        assert data["error"]["code"] == 503
        assert data["error"]["message"] == "Service Unavailable"
        details = " ".join(data["error"].get("details") or [])
        assert "Redis-backed durable storage" in details
