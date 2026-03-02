"""Admin API client for Honua Server."""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any

import httpx

from ..errors import HonuaHttpError
from ._models import (
    AdminCapabilitiesResponse,
    AdminVersionResponse,
    ConnectionTestResult,
    CreateSecureConnectionRequest,
    EncryptionValidationResult,
    KeyRotationResult,
    LayerStyleResponse,
    LayerStyleUpdateRequest,
    ManifestApplyRequest,
    ManifestApplyResult,
    MetadataManifest,
    MetadataResource,
    PublishLayerRequest,
    PublishedLayerSummary,
    SecureConnectionDetail,
    SecureConnectionSummary,
    ServiceSettingsResponse,
    ServiceSummary,
    TableDiscoveryResponse,
    UpdateSecureConnectionRequest,
)


def _normalize_base_url(base_url: str) -> str:
    return base_url.rstrip("/") + "/"


class HonuaAdminClient:
    """Synchronous client for the Honua Admin API."""

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

    # -- context manager ----------------------------------------------------

    def __enter__(self) -> HonuaAdminClient:
        return self

    def __exit__(self, exc_type: object, exc: object, tb: object) -> None:
        self.close()

    def close(self) -> None:
        """Release underlying resources if this instance owns the HTTP client."""
        if self._owns_client:
            self._client.close()

    # -- internal helpers ---------------------------------------------------

    def _request(
        self,
        method: str,
        path: str,
        *,
        params: Mapping[str, Any] | None = None,
        json_body: Any | None = None,
        headers: dict[str, str] | None = None,
    ) -> httpx.Response:
        response = self._client.request(
            method=method,
            url=path,
            params=params,
            json=json_body,
            headers=headers,
        )
        if response.status_code >= 400:
            raise self._to_http_error(response)
        return response

    def _request_json(
        self,
        method: str,
        path: str,
        *,
        params: Mapping[str, Any] | None = None,
        json_body: Any | None = None,
        headers: dict[str, str] | None = None,
    ) -> Any:
        """Issue a request and unwrap the ApiResponse envelope.

        The Honua admin API wraps every successful response in::

            {"success": true, "data": <payload>, "message": "...", "timestamp": "..."}

        This method returns the inner ``data`` value.
        """
        response = self._request(method, path, params=params, json_body=json_body, headers=headers)
        if not response.content:
            return None

        try:
            payload = response.json()
        except ValueError:
            return response.text

        if isinstance(payload, Mapping) and "data" in payload:
            return payload["data"]
        return payload

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

    # ======================================================================
    # Services
    # ======================================================================

    def list_services(self) -> list[ServiceSummary]:
        """GET /api/v1/admin/services/"""
        data = self._request_json("GET", "/api/v1/admin/services/")
        if not isinstance(data, list):
            return []
        return [ServiceSummary.from_dict(s) for s in data]

    def get_service_settings(self, name: str) -> ServiceSettingsResponse:
        """GET /api/v1/admin/services/{name}/settings"""
        data = self._request_json("GET", f"/api/v1/admin/services/{name}/settings")
        return ServiceSettingsResponse.from_dict(data)

    def update_protocols(self, name: str, protocols: list[str]) -> ServiceSettingsResponse:
        """PUT /api/v1/admin/services/{name}/protocols"""
        data = self._request_json(
            "PUT",
            f"/api/v1/admin/services/{name}/protocols",
            json_body=protocols,
        )
        return ServiceSettingsResponse.from_dict(data)

    def update_mapserver_settings(self, name: str, **kwargs: Any) -> ServiceSettingsResponse:
        """PUT /api/v1/admin/services/{name}/mapserver"""
        from ._models import _to_camel

        body = {_to_camel(k): v for k, v in kwargs.items()}
        data = self._request_json(
            "PUT",
            f"/api/v1/admin/services/{name}/mapserver",
            json_body=body,
        )
        return ServiceSettingsResponse.from_dict(data)

    # ======================================================================
    # Metadata Resources
    # ======================================================================

    def list_metadata_resources(
        self,
        kind: str | None = None,
        namespace: str | None = None,
    ) -> list[MetadataResource]:
        """GET /api/v1/admin/metadata/resources"""
        params: dict[str, str] = {}
        if kind is not None:
            params["kind"] = kind
        if namespace is not None:
            params["namespace"] = namespace
        data = self._request_json(
            "GET",
            "/api/v1/admin/metadata/resources",
            params=params or None,
        )
        if not isinstance(data, list):
            return []
        return [MetadataResource.from_dict(r) for r in data]

    def get_metadata_resource(
        self,
        kind: str,
        ns: str,
        name: str,
    ) -> tuple[MetadataResource, str | None]:
        """GET /api/v1/admin/metadata/resources/{kind}/{ns}/{name}

        Returns ``(resource, etag)`` where *etag* may be ``None`` if the
        server did not include an ``ETag`` header.
        """
        response = self._request(
            "GET",
            f"/api/v1/admin/metadata/resources/{kind}/{ns}/{name}",
        )
        payload = response.json()
        inner = payload.get("data", payload) if isinstance(payload, Mapping) else payload
        resource = MetadataResource.from_dict(inner)
        etag = response.headers.get("ETag") or response.headers.get("etag")
        return resource, etag

    def create_metadata_resource(self, resource: MetadataResource) -> MetadataResource:
        """POST /api/v1/admin/metadata/resources"""
        data = self._request_json(
            "POST",
            "/api/v1/admin/metadata/resources",
            json_body=resource.to_dict(),
        )
        return MetadataResource.from_dict(data)

    def update_metadata_resource(
        self,
        kind: str,
        ns: str,
        name: str,
        resource: MetadataResource,
        *,
        if_match: str | None = None,
    ) -> MetadataResource:
        """PUT /api/v1/admin/metadata/resources/{kind}/{ns}/{name}"""
        hdrs: dict[str, str] = {}
        if if_match is not None:
            hdrs["If-Match"] = if_match
        data = self._request_json(
            "PUT",
            f"/api/v1/admin/metadata/resources/{kind}/{ns}/{name}",
            json_body=resource.to_dict(),
            headers=hdrs or None,
        )
        return MetadataResource.from_dict(data)

    def delete_metadata_resource(
        self,
        kind: str,
        ns: str,
        name: str,
        *,
        if_match: str | None = None,
    ) -> None:
        """DELETE /api/v1/admin/metadata/resources/{kind}/{ns}/{name}"""
        hdrs: dict[str, str] = {}
        if if_match is not None:
            hdrs["If-Match"] = if_match
        self._request(
            "DELETE",
            f"/api/v1/admin/metadata/resources/{kind}/{ns}/{name}",
            headers=hdrs or None,
        )

    # ======================================================================
    # Manifests
    # ======================================================================

    def get_version(self) -> AdminVersionResponse:
        """GET /api/v1/admin/version"""
        data = self._request_json("GET", "/api/v1/admin/version")
        return AdminVersionResponse.from_dict(data)

    def get_capabilities(self) -> AdminCapabilitiesResponse:
        """GET /api/v1/admin/capabilities"""
        data = self._request_json("GET", "/api/v1/admin/capabilities")
        return AdminCapabilitiesResponse.from_dict(data)

    def get_manifest(self, namespace: str | None = None) -> MetadataManifest:
        """GET /api/v1/admin/manifest"""
        params: dict[str, str] | None = None
        if namespace is not None:
            params = {"namespace": namespace}
        data = self._request_json("GET", "/api/v1/admin/manifest", params=params)
        return MetadataManifest.from_dict(data)

    def apply_manifest(self, request: ManifestApplyRequest) -> ManifestApplyResult:
        """POST /api/v1/admin/manifest/apply"""
        data = self._request_json(
            "POST",
            "/api/v1/admin/manifest/apply",
            json_body=request.to_dict(),
        )
        return ManifestApplyResult.from_dict(data)

    # ======================================================================
    # Connections
    # ======================================================================

    def list_connections(self) -> list[SecureConnectionSummary]:
        """GET /api/v1/admin/connections"""
        data = self._request_json("GET", "/api/v1/admin/connections")
        if not isinstance(data, list):
            return []
        return [SecureConnectionSummary.from_dict(c) for c in data]

    def get_connection(self, id: str) -> SecureConnectionDetail:
        """GET /api/v1/admin/connections/{id}"""
        data = self._request_json("GET", f"/api/v1/admin/connections/{id}")
        return SecureConnectionDetail.from_dict(data)

    def create_connection(self, request: CreateSecureConnectionRequest) -> SecureConnectionSummary:
        """POST /api/v1/admin/connections"""
        data = self._request_json(
            "POST",
            "/api/v1/admin/connections",
            json_body=request.to_dict(),
        )
        return SecureConnectionSummary.from_dict(data)

    def test_draft_connection(self, request: CreateSecureConnectionRequest) -> ConnectionTestResult:
        """POST /api/v1/admin/connections/test"""
        data = self._request_json(
            "POST",
            "/api/v1/admin/connections/test",
            json_body=request.to_dict(),
        )
        return ConnectionTestResult.from_dict(data)

    def update_connection(
        self,
        id: str,
        request: UpdateSecureConnectionRequest,
    ) -> SecureConnectionSummary:
        """PUT /api/v1/admin/connections/{id}"""
        data = self._request_json(
            "PUT",
            f"/api/v1/admin/connections/{id}",
            json_body=request.to_dict(),
        )
        return SecureConnectionSummary.from_dict(data)

    def test_connection(self, id: str) -> ConnectionTestResult:
        """POST /api/v1/admin/connections/{id}/test"""
        data = self._request_json(
            "POST",
            f"/api/v1/admin/connections/{id}/test",
        )
        return ConnectionTestResult.from_dict(data)

    def delete_connection(self, id: str) -> None:
        """DELETE /api/v1/admin/connections/{id}"""
        self._request("DELETE", f"/api/v1/admin/connections/{id}")

    def validate_encryption(self) -> EncryptionValidationResult:
        """POST /api/v1/admin/connections/encryption/validate"""
        data = self._request_json(
            "POST",
            "/api/v1/admin/connections/encryption/validate",
        )
        return EncryptionValidationResult.from_dict(data)

    def rotate_encryption_key(self) -> KeyRotationResult:
        """POST /api/v1/admin/connections/encryption/rotate-key"""
        data = self._request_json(
            "POST",
            "/api/v1/admin/connections/encryption/rotate-key",
        )
        return KeyRotationResult.from_dict(data)

    # ======================================================================
    # Layers
    # ======================================================================

    def list_layers(
        self,
        conn_id: str,
        service_name: str | None = None,
    ) -> list[PublishedLayerSummary]:
        """GET /api/v1/admin/connections/{conn_id}/layers"""
        params: dict[str, str] | None = None
        if service_name is not None:
            params = {"serviceName": service_name}
        data = self._request_json(
            "GET",
            f"/api/v1/admin/connections/{conn_id}/layers",
            params=params,
        )
        if not isinstance(data, list):
            return []
        return [PublishedLayerSummary.from_dict(l) for l in data]

    def publish_layer(
        self,
        conn_id: str,
        request: PublishLayerRequest,
    ) -> PublishedLayerSummary:
        """POST /api/v1/admin/connections/{conn_id}/layers"""
        data = self._request_json(
            "POST",
            f"/api/v1/admin/connections/{conn_id}/layers",
            json_body=request.to_dict(),
        )
        return PublishedLayerSummary.from_dict(data)

    def set_layer_enabled(
        self,
        conn_id: str,
        layer_id: int,
        enabled: bool,
        service_name: str | None = None,
    ) -> PublishedLayerSummary:
        """PUT /api/v1/admin/connections/{conn_id}/layers/{layer_id}/enabled"""
        params: dict[str, str] | None = None
        if service_name is not None:
            params = {"serviceName": service_name}
        data = self._request_json(
            "PUT",
            f"/api/v1/admin/connections/{conn_id}/layers/{layer_id}/enabled",
            json_body={"enabled": enabled},
            params=params,
        )
        return PublishedLayerSummary.from_dict(data)

    def set_service_layers_enabled(
        self,
        conn_id: str,
        enabled: bool,
        service_name: str | None = None,
    ) -> list[PublishedLayerSummary]:
        """PUT /api/v1/admin/connections/{conn_id}/layers/enabled"""
        params: dict[str, str] | None = None
        if service_name is not None:
            params = {"serviceName": service_name}
        data = self._request_json(
            "PUT",
            f"/api/v1/admin/connections/{conn_id}/layers/enabled",
            json_body={"enabled": enabled},
            params=params,
        )
        if not isinstance(data, list):
            return []
        return [PublishedLayerSummary.from_dict(l) for l in data]

    # ======================================================================
    # Discovery
    # ======================================================================

    def discover_tables(self, conn_id: str) -> TableDiscoveryResponse:
        """GET /api/v1/admin/connections/{conn_id}/tables"""
        data = self._request_json(
            "GET",
            f"/api/v1/admin/connections/{conn_id}/tables",
        )
        return TableDiscoveryResponse.from_dict(data)

    # ======================================================================
    # Styles
    # ======================================================================

    def get_layer_style(self, layer_id: int) -> LayerStyleResponse:
        """GET /api/v1/admin/metadata/layers/{layer_id}/style"""
        data = self._request_json(
            "GET",
            f"/api/v1/admin/metadata/layers/{layer_id}/style",
        )
        return LayerStyleResponse.from_dict(data)

    def update_layer_style(
        self,
        layer_id: int,
        request: LayerStyleUpdateRequest,
    ) -> LayerStyleResponse:
        """PUT /api/v1/admin/metadata/layers/{layer_id}/style"""
        data = self._request_json(
            "PUT",
            f"/api/v1/admin/metadata/layers/{layer_id}/style",
            json_body=request.to_dict(),
        )
        return LayerStyleResponse.from_dict(data)

    # ======================================================================
    # Config
    # ======================================================================

    def get_config(self) -> dict[str, Any]:
        """GET /api/v1/admin/config"""
        data = self._request_json("GET", "/api/v1/admin/config")
        if isinstance(data, dict):
            return data
        return {}
