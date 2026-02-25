"""Synchronous HTTP client for Honua Server APIs."""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from typing import Any

import httpx

from .errors import HonuaHttpError


def _bool_text(value: bool) -> str:
    return "true" if value else "false"


def _normalize_base_url(base_url: str) -> str:
    return base_url.rstrip("/") + "/"


class HonuaClient:
    """Task-oriented client for common Honua workflows."""

    def __init__(
        self,
        base_url: str,
        *,
        timeout: float = 30.0,
        api_key: str | None = None,
        bearer_token: str | None = None,
        client: httpx.Client | None = None,
        transport: httpx.BaseTransport | None = None,
    ) -> None:
        if client is not None and transport is not None:
            raise ValueError("Provide either `client` or `transport`, not both.")

        self._owns_client = client is None
        if client is not None:
            self._client = client
            return

        headers: dict[str, str] = {}
        if api_key:
            headers["X-API-Key"] = api_key
        if bearer_token:
            headers["Authorization"] = f"Bearer {bearer_token}"

        self._client = httpx.Client(
            base_url=_normalize_base_url(base_url),
            timeout=timeout,
            headers=headers,
            follow_redirects=True,
            transport=transport,
        )

    def __enter__(self) -> "HonuaClient":
        return self

    def __exit__(self, exc_type: object, exc: object, tb: object) -> None:
        self.close()

    def close(self) -> None:
        """Release underlying resources if this instance owns the HTTP client."""
        if self._owns_client:
            self._client.close()

    def readiness(self) -> dict[str, Any]:
        """Get readiness status from `/healthz/ready`."""
        return self._request_json("GET", "/healthz/ready")

    def list_services(self, *, response_format: str = "json") -> dict[str, Any]:
        """List services from the GeoServices catalog endpoint."""
        return self._request_json(
            "GET",
            "/rest/services",
            params={"f": response_format},
        )

    def query_features(
        self,
        service_id: str,
        layer_id: int,
        *,
        where: str = "1=1",
        out_fields: str | Sequence[str] = "*",
        return_geometry: bool = True,
        extra_params: Mapping[str, Any] | None = None,
    ) -> dict[str, Any]:
        """Query features from a FeatureServer layer."""
        if isinstance(out_fields, Sequence) and not isinstance(out_fields, str):
            out_fields_value = ",".join(str(value) for value in out_fields)
        else:
            out_fields_value = str(out_fields)

        params: dict[str, Any] = {
            "f": "json",
            "where": where,
            "outFields": out_fields_value,
            "returnGeometry": _bool_text(return_geometry),
        }
        if extra_params:
            params.update(extra_params)

        return self._request_json(
            "GET",
            f"/rest/services/{service_id}/FeatureServer/{layer_id}/query",
            params=params,
        )

    def apply_edits(
        self,
        service_id: str,
        layer_id: int,
        *,
        adds: Sequence[Mapping[str, Any]] | None = None,
        updates: Sequence[Mapping[str, Any]] | None = None,
        deletes: Sequence[int] | str | None = None,
        rollback_on_failure: bool = True,
    ) -> dict[str, Any]:
        """Submit a layer-level applyEdits request."""
        payload: dict[str, Any] = {
            "f": "json",
            "rollbackOnFailure": rollback_on_failure,
        }
        if adds is not None:
            payload["adds"] = list(adds)
        if updates is not None:
            payload["updates"] = list(updates)
        if deletes is not None:
            if isinstance(deletes, str):
                payload["deletes"] = deletes
            else:
                payload["deletes"] = list(deletes)

        return self._request_json(
            "POST",
            f"/rest/services/{service_id}/FeatureServer/{layer_id}/applyEdits",
            json_body=payload,
        )

    def export_map(
        self,
        service_id: str,
        bbox: Sequence[float] | str,
        *,
        size: tuple[int, int] = (400, 400),
        image_format: str = "png",
        transparent: bool = True,
        dpi: int = 96,
        extra_params: Mapping[str, Any] | None = None,
    ) -> bytes:
        """Request rendered map bytes from MapServer export."""
        if isinstance(bbox, str):
            bbox_value = bbox
        else:
            bbox_value = ",".join(str(value) for value in bbox)

        params: dict[str, Any] = {
            "f": "image",
            "bbox": bbox_value,
            "size": f"{size[0]},{size[1]}",
            "format": image_format,
            "transparent": _bool_text(transparent),
            "dpi": str(dpi),
        }
        if extra_params:
            params.update(extra_params)

        response = self._request("GET", f"/rest/services/{service_id}/MapServer/export", params=params)
        return response.content

    def _request_json(
        self,
        method: str,
        path: str,
        *,
        params: Mapping[str, Any] | None = None,
        json_body: Mapping[str, Any] | None = None,
    ) -> dict[str, Any]:
        response = self._request(method, path, params=params, json_body=json_body)
        if not response.content:
            return {}

        try:
            payload = response.json()
        except ValueError:
            return {"raw": response.text}

        if isinstance(payload, Mapping):
            return dict(payload)
        return {"data": payload}

    def _request(
        self,
        method: str,
        path: str,
        *,
        params: Mapping[str, Any] | None = None,
        json_body: Mapping[str, Any] | None = None,
    ) -> httpx.Response:
        response = self._client.request(
            method=method,
            url=path,
            params=params,
            json=json_body,
        )
        if response.status_code >= 400:
            raise self._to_http_error(response)
        return response

    @staticmethod
    def _to_http_error(response: httpx.Response) -> HonuaHttpError:
        body: Any | None = None
        message = response.reason_phrase or "Request failed"

        if response.content:
            try:
                body = response.json()
            except ValueError:
                body = response.text
        if isinstance(body, Mapping):
            error = body.get("error")
            if isinstance(error, Mapping):
                candidate = error.get("message")
                if isinstance(candidate, str) and candidate:
                    message = candidate
            else:
                candidate = body.get("detail") or body.get("message")
                if isinstance(candidate, str) and candidate:
                    message = candidate

        return HonuaHttpError(response.status_code, message, body=body)
