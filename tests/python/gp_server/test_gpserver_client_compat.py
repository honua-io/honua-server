# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Python client-style coverage for the GeoServices GPServer surface."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

import httpx
import pytest

POINT_WKB_BASE64 = "AQEAAAAAAAAAAAAAAAAAAAAAAAAA"


@dataclass(frozen=True)
class GPServerClient:
    http: httpx.Client
    service_id: str

    def service_info(self) -> dict[str, Any]:
        response = self.http.get(
            f"/rest/services/{self.service_id}/GPServer",
            params={"f": "json"},
        )
        response.raise_for_status()
        return response.json()

    def task_info(self, task_name: str) -> dict[str, Any]:
        response = self.http.get(
            f"/rest/services/{self.service_id}/GPServer/{task_name}",
            params={"f": "json"},
        )
        response.raise_for_status()
        return response.json()

    def submit_buffer_job(self) -> httpx.Response:
        return self.http.post(
            f"/rest/services/{self.service_id}/GPServer/geometry.buffer/submitJob",
            data={
                "f": "json",
                "wkb": POINT_WKB_BASE64,
                "srid": "4326",
                "distance": "10",
            },
        )

    def job_status(self, job_id: str) -> httpx.Response:
        return self.http.get(
            f"/rest/services/{self.service_id}/GPServer/geometry.buffer/jobs/{job_id}",
            params={"f": "json"},
        )

    def missing_job_status(self) -> httpx.Response:
        return self.http.get(
            f"/rest/services/{self.service_id}/GPServer/geometry.buffer/jobs/missing-python-client-job",
            params={"f": "json"},
        )


def _assert_esri_error(response: httpx.Response, expected_code: int) -> dict[str, Any]:
    data = response.json()
    assert data["error"]["code"] == expected_code
    assert data["error"]["message"]
    assert isinstance(data["error"].get("details"), list)
    return data


@pytest.mark.integration
@pytest.mark.gpserver
def test_gpserver_python_client_metadata_workflow(
    http_client: httpx.Client, test_service_id: str
):
    client = GPServerClient(http_client, test_service_id)

    service = client.service_info()
    task = client.task_info("geometry.buffer")

    assert service["currentVersion"] == 10.81
    assert service["executionType"] == "esriExecutionTypeAsynchronous"
    assert "geometry.buffer" in service["tasks"]
    assert task["name"] == "geometry.buffer"
    assert task["executionType"] == "esriExecutionTypeAsynchronous"
    assert any(param["name"] == "wkb" for param in task["parameters"])
    assert any(param["name"] == "distance" for param in task["parameters"])


@pytest.mark.integration
@pytest.mark.gpserver
def test_gpserver_python_client_submit_and_poll_workflow(
    http_client: httpx.Client, test_service_id: str
):
    client = GPServerClient(http_client, test_service_id)

    submit = client.submit_buffer_job()
    assert submit.status_code in (200, 503)
    submit_data = submit.json()

    if submit.status_code == 503:
        error = _assert_esri_error(submit, 503)
        details = " ".join(error["error"].get("details") or [])
        assert "Redis-backed durable storage" in details
        return

    assert submit_data["jobId"]
    assert submit_data["jobStatus"] == "esriJobSubmitted"

    status = client.job_status(submit_data["jobId"])
    assert status.status_code == 200
    status_data = status.json()
    assert status_data["jobId"] == submit_data["jobId"]
    assert status_data["jobStatus"].startswith("esriJob")


@pytest.mark.integration
@pytest.mark.gpserver
def test_gpserver_python_client_missing_job_uses_esri_error_shape(
    http_client: httpx.Client, test_service_id: str
):
    client = GPServerClient(http_client, test_service_id)

    response = client.missing_job_status()

    assert response.status_code in (404, 503)
    _assert_esri_error(response, response.status_code)
