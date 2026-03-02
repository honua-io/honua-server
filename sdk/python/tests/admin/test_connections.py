"""Tests for admin connection endpoints."""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest

from honua_sdk import HonuaHttpError
from honua_sdk.admin import (
    ConnectionTestResult,
    CreateSecureConnectionRequest,
    EncryptionValidationResult,
    HonuaAdminClient,
    KeyRotationResult,
    SecureConnectionDetail,
    SecureConnectionSummary,
    UpdateSecureConnectionRequest,
)
from .conftest import make_api_response


_CONN_SUMMARY = {
    "connectionId": "conn-001",
    "name": "prod-db",
    "description": "Production database",
    "host": "db.example.com",
    "port": 5432,
    "databaseName": "honua",
    "username": "admin",
    "sslRequired": True,
    "sslMode": "require",
    "storageType": "PostgreSQL",
    "isActive": True,
    "healthStatus": "healthy",
    "lastHealthCheck": "2026-03-01T00:00:00Z",
    "createdAt": "2026-01-15T00:00:00Z",
    "createdBy": "system",
}


def test_list_connections(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        return httpx.Response(200, json=make_api_response([_CONN_SUMMARY]))

    with make_client(handler) as client:
        result = client.list_connections()

    assert seen["method"] == "GET"
    assert seen["path"] == "/api/v1/admin/connections"
    assert len(result) == 1
    assert isinstance(result[0], SecureConnectionSummary)
    assert result[0].connection_id == "conn-001"
    assert result[0].name == "prod-db"
    assert result[0].ssl_required is True


def test_get_connection_returns_detail(make_client) -> None:
    detail_data = {
        **_CONN_SUMMARY,
        "credentialReference": "vault://secret/db",
        "encryptionVersion": 2,
        "updatedAt": "2026-02-20T00:00:00Z",
    }

    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.path == "/api/v1/admin/connections/conn-001"
        return httpx.Response(200, json=make_api_response(detail_data))

    with make_client(handler) as client:
        result = client.get_connection("conn-001")

    assert isinstance(result, SecureConnectionDetail)
    assert result.credential_reference == "vault://secret/db"
    assert result.encryption_version == 2
    assert result.updated_at == "2026-02-20T00:00:00Z"


def test_create_connection_sends_request_body(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["body"] = json.loads(request.content.decode("utf-8"))
        return httpx.Response(201, json=make_api_response(_CONN_SUMMARY))

    req = CreateSecureConnectionRequest(
        name="prod-db",
        host="db.example.com",
        port=5432,
        database_name="honua",
        username="admin",
        password="secret123",
        ssl_required=True,
    )

    with make_client(handler) as client:
        result = client.create_connection(req)

    assert seen["method"] == "POST"
    assert seen["body"]["name"] == "prod-db"
    assert seen["body"]["host"] == "db.example.com"
    assert seen["body"]["password"] == "secret123"
    assert seen["body"]["sslRequired"] is True
    assert isinstance(result, SecureConnectionSummary)


def test_test_draft_connection(make_client) -> None:
    test_result = {
        "connectionId": None,
        "connectionName": "test-conn",
        "isHealthy": True,
        "testedAt": "2026-03-01T00:00:00Z",
        "message": "Connection successful",
    }

    def handler(request: httpx.Request) -> httpx.Response:
        assert request.url.path == "/api/v1/admin/connections/test"
        return httpx.Response(200, json=make_api_response(test_result))

    req = CreateSecureConnectionRequest(
        name="test-conn",
        host="db.example.com",
        port=5432,
        database_name="honua",
        username="admin",
        password="secret123",
    )

    with make_client(handler) as client:
        result = client.test_draft_connection(req)

    assert isinstance(result, ConnectionTestResult)
    assert result.is_healthy is True
    assert result.message == "Connection successful"


def test_update_connection_sends_partial_body(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["body"] = json.loads(request.content.decode("utf-8"))
        assert request.url.path == "/api/v1/admin/connections/conn-001"
        return httpx.Response(200, json=make_api_response(_CONN_SUMMARY))

    req = UpdateSecureConnectionRequest(description="Updated desc", port=5433)

    with make_client(handler) as client:
        result = client.update_connection("conn-001", req)

    assert seen["body"]["description"] == "Updated desc"
    assert seen["body"]["port"] == 5433
    # password should not be present since it was not set
    assert "password" not in seen["body"]
    assert isinstance(result, SecureConnectionSummary)


def test_test_connection_by_id(make_client) -> None:
    test_result = {
        "connectionId": "conn-001",
        "connectionName": "prod-db",
        "isHealthy": True,
        "testedAt": "2026-03-01T00:00:00Z",
        "message": "OK",
    }

    def handler(request: httpx.Request) -> httpx.Response:
        assert request.method == "POST"
        assert request.url.path == "/api/v1/admin/connections/conn-001/test"
        return httpx.Response(200, json=make_api_response(test_result))

    with make_client(handler) as client:
        result = client.test_connection("conn-001")

    assert isinstance(result, ConnectionTestResult)
    assert result.connection_id == "conn-001"
    assert result.is_healthy is True


def test_delete_connection(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        return httpx.Response(204)

    with make_client(handler) as client:
        client.delete_connection("conn-001")

    assert seen["method"] == "DELETE"
    assert seen["path"] == "/api/v1/admin/connections/conn-001"


def test_validate_encryption(make_client) -> None:
    enc_data = {
        "isValid": True,
        "currentKeyVersion": 3,
        "validatedAt": "2026-03-01T00:00:00Z",
        "message": "All keys valid",
    }

    def handler(request: httpx.Request) -> httpx.Response:
        assert request.method == "POST"
        assert request.url.path == "/api/v1/admin/connections/encryption/validate"
        return httpx.Response(200, json=make_api_response(enc_data))

    with make_client(handler) as client:
        result = client.validate_encryption()

    assert isinstance(result, EncryptionValidationResult)
    assert result.is_valid is True
    assert result.current_key_version == 3


def test_rotate_encryption_key(make_client) -> None:
    rotation_data = {
        "previousKeyVersion": 3,
        "newKeyVersion": 4,
        "rotatedAt": "2026-03-01T00:00:00Z",
        "message": "Key rotated successfully",
    }

    def handler(request: httpx.Request) -> httpx.Response:
        assert request.method == "POST"
        assert request.url.path == "/api/v1/admin/connections/encryption/rotate-key"
        return httpx.Response(200, json=make_api_response(rotation_data))

    with make_client(handler) as client:
        result = client.rotate_encryption_key()

    assert isinstance(result, KeyRotationResult)
    assert result.previous_key_version == 3
    assert result.new_key_version == 4


def test_get_connection_404_raises_error(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(404, json={"message": "Connection not found"})

    with make_client(handler) as client:
        with pytest.raises(HonuaHttpError) as exc_info:
            client.get_connection("nonexistent")

    assert exc_info.value.status_code == 404


def test_create_connection_409_conflict(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(409, json={"message": "Connection already exists"})

    req = CreateSecureConnectionRequest(name="dup", host="h", database_name="d", username="u")

    with make_client(handler) as client:
        with pytest.raises(HonuaHttpError) as exc_info:
            client.create_connection(req)

    assert exc_info.value.status_code == 409
