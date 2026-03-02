"""Tests for admin manifest and version/capabilities endpoints."""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest

from honua_sdk import HonuaHttpError
from honua_sdk.admin import (
    AdminCapabilitiesResponse,
    AdminVersionResponse,
    HonuaAdminClient,
    ManifestApplyRequest,
    ManifestApplyResult,
    MetadataManifest,
    MetadataResource,
    ResourceMetadata,
)
from .conftest import make_api_response


def test_get_version(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        return httpx.Response(
            200,
            json=make_api_response({
                "version": "1.2.0",
                "metadataApiVersion": "v1",
                "serverTime": "2026-03-01T00:00:00Z",
            }),
        )

    with make_client(handler) as client:
        result = client.get_version()

    assert seen["method"] == "GET"
    assert seen["path"] == "/api/v1/admin/version"
    assert isinstance(result, AdminVersionResponse)
    assert result.version == "1.2.0"
    assert result.metadata_api_version == "v1"


def test_get_capabilities(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["path"] = request.url.path
        return httpx.Response(
            200,
            json=make_api_response({
                "metadataApiVersions": ["v1"],
                "resourceKinds": ["Layer", "Service", "Connection"],
                "manifestSupported": True,
                "manifestDryRunSupported": True,
                "manifestPruneSupported": False,
            }),
        )

    with make_client(handler) as client:
        result = client.get_capabilities()

    assert seen["path"] == "/api/v1/admin/capabilities"
    assert isinstance(result, AdminCapabilitiesResponse)
    assert result.metadata_api_versions == ["v1"]
    assert result.resource_kinds == ["Layer", "Service", "Connection"]
    assert result.manifest_supported is True
    assert result.manifest_dry_run_supported is True
    assert result.manifest_prune_supported is False


def test_get_manifest_without_namespace(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["path"] = request.url.path
        seen["params"] = dict(request.url.params.multi_items())
        return httpx.Response(
            200,
            json=make_api_response({
                "apiVersion": "v1",
                "generatedAt": "2026-03-01T00:00:00Z",
                "resources": [
                    {
                        "apiVersion": "v1",
                        "kind": "Layer",
                        "metadata": {
                            "name": "parcels",
                            "namespace": "default",
                            "labels": {},
                            "annotations": {},
                        },
                        "spec": {},
                    },
                ],
                "driftedResources": [],
                "manifestHash": "sha256:abc123",
            }),
        )

    with make_client(handler) as client:
        result = client.get_manifest()

    assert seen["path"] == "/api/v1/admin/manifest"
    assert seen["params"] == {}
    assert isinstance(result, MetadataManifest)
    assert result.api_version == "v1"
    assert result.manifest_hash == "sha256:abc123"
    assert len(result.resources) == 1
    assert result.resources[0].kind == "Layer"
    assert result.drifted_resources == []


def test_get_manifest_with_namespace(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["params"] = dict(request.url.params.multi_items())
        return httpx.Response(
            200,
            json=make_api_response({
                "apiVersion": "v1",
                "generatedAt": None,
                "resources": [],
                "driftedResources": [],
                "manifestHash": None,
            }),
        )

    with make_client(handler) as client:
        client.get_manifest(namespace="staging")

    assert seen["params"]["namespace"] == "staging"


def test_apply_manifest(make_client) -> None:
    seen: dict[str, Any] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["method"] = request.method
        seen["path"] = request.url.path
        seen["body"] = json.loads(request.content.decode("utf-8"))
        return httpx.Response(
            200,
            json=make_api_response({
                "dryRun": True,
                "summary": {
                    "created": 1,
                    "updated": 0,
                    "deleted": 0,
                    "skipped": 0,
                },
                "entries": [
                    {
                        "action": "Create",
                        "resource": {
                            "kind": "Layer",
                            "namespace": "default",
                            "name": "parcels",
                        },
                        "message": "Would create Layer default/parcels",
                    },
                ],
            }),
        )

    resource = MetadataResource(
        api_version="v1",
        kind="Layer",
        metadata=ResourceMetadata(
            id=None,
            name="parcels",
            namespace="default",
            labels={},
            annotations={},
            resource_version=None,
            generation=None,
            created_at=None,
            updated_at=None,
        ),
        spec={"tableName": "parcels"},
        status=None,
    )

    req = ManifestApplyRequest(resources=[resource], dry_run=True, prune=False)

    with make_client(handler) as client:
        result = client.apply_manifest(req)

    assert seen["method"] == "POST"
    assert seen["path"] == "/api/v1/admin/manifest/apply"
    assert seen["body"]["dryRun"] is True
    assert seen["body"]["prune"] is False
    assert len(seen["body"]["resources"]) == 1

    assert isinstance(result, ManifestApplyResult)
    assert result.dry_run is True
    assert result.summary.created == 1
    assert result.summary.updated == 0
    assert len(result.entries) == 1
    assert result.entries[0].action == "Create"
    assert result.entries[0].resource.kind == "Layer"
    assert result.entries[0].resource.name == "parcels"
    assert result.entries[0].message == "Would create Layer default/parcels"


def test_get_version_error(make_client) -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(400, json={"message": "Bad request"})

    with make_client(handler) as client:
        with pytest.raises(HonuaHttpError) as exc_info:
            client.get_version()

    assert exc_info.value.status_code == 400
