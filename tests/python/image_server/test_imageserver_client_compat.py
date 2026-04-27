# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Python client-style coverage for the GeoServices ImageServer surface."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

import httpx
import pytest


@dataclass(frozen=True)
class ImageServerClient:
    http: httpx.Client
    layer_id: int

    def service_info(self, f: str = "json") -> httpx.Response:
        return self.http.get(
            f"/rest/services/{self.layer_id}/ImageServer",
            params={"f": f},
        )

    def export_image(self, f: str = "json") -> httpx.Response:
        return self.http.get(
            f"/rest/services/{self.layer_id}/ImageServer/exportImage",
            params={
                "bbox": "-180,-90,180,90",
                "size": "64,64",
                "format": "png",
                "f": f,
            },
        )

    def identify(self, **params: str) -> httpx.Response:
        values = {"f": "json", **params}
        return self.http.get(
            f"/rest/services/{self.layer_id}/ImageServer/identify",
            params=values,
        )


def _assert_esri_error(response: httpx.Response, expected_code: int) -> dict[str, Any]:
    data = response.json()
    assert data["error"]["code"] == expected_code
    assert data["error"]["message"]
    assert isinstance(data["error"].get("details"), list)
    return data


@pytest.mark.integration
@pytest.mark.imageserver
def test_imageserver_python_client_metadata(
    http_client: httpx.Client, test_layer_id: int
):
    client = ImageServerClient(http_client, test_layer_id)

    response = client.service_info()

    assert response.status_code == 200, response.text
    data = response.json()
    assert data["currentVersion"] == 10.81
    assert "Image" in data["capabilities"]
    assert data["bandCount"] > 0


@pytest.mark.integration
@pytest.mark.imageserver
def test_imageserver_python_client_export_metadata(
    http_client: httpx.Client, test_layer_id: int
):
    client = ImageServerClient(http_client, test_layer_id)

    response = client.export_image(f="json")

    assert response.status_code == 200, response.text
    data = response.json()
    assert data["width"] == 64
    assert data["height"] == 64
    assert data["href"]
    assert "extent" in data


@pytest.mark.integration
@pytest.mark.imageserver
def test_imageserver_python_client_error_shapes(
    http_client: httpx.Client, test_layer_id: int
):
    client = ImageServerClient(http_client, test_layer_id)

    invalid_format = client.service_info(f="xml")
    invalid_identify_format = client.identify(geometry="0,0", f="xml")

    assert invalid_format.status_code == 400
    invalid_format_error = _assert_esri_error(invalid_format, 400)
    assert "Only JSON format is supported" in " ".join(
        invalid_format_error["error"].get("details") or []
    )

    assert invalid_identify_format.status_code == 400
    identify_error = _assert_esri_error(invalid_identify_format, 400)
    assert "Only JSON format is supported" in " ".join(
        identify_error["error"].get("details") or []
    )
