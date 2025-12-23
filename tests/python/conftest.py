# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Shared pytest fixtures for Honua integration tests.

This module provides fixtures used by both OGC API Features and
GeoServices REST/FeatureServer test suites.
"""

from __future__ import annotations

import os
from typing import Generator

import httpx
import pytest

from shared.geometry import GeometryGenerator, TestGeometry
from shared.postgis import PostGISFixture, TestDataBuilder
from shared.server import HonuaServer


# =============================================================================
# Session-scoped fixtures (shared across all tests)
# =============================================================================


@pytest.fixture(scope="session")
def postgis() -> Generator[PostGISFixture, None, None]:
    """
    Session-scoped PostGIS container fixture.

    Starts once per test session and is shared by all tests.
    Individual tests should use schema isolation for data separation.
    """
    fixture = PostGISFixture()
    fixture.start()
    yield fixture
    fixture.stop()


@pytest.fixture(scope="session")
def honua_server(postgis: PostGISFixture) -> Generator[HonuaServer, None, None]:
    """
    Session-scoped Honua server fixture.

    Starts the server once per session connected to the test PostGIS instance.
    """
    server = HonuaServer(
        connection_string=postgis.connection_string,
        port=5555,
    )
    server.start(timeout=120)
    yield server
    server.stop()


@pytest.fixture(scope="session")
def base_url(honua_server: HonuaServer) -> str:
    """Get the base URL of the running Honua server."""
    return honua_server.base_url


# =============================================================================
# Function-scoped fixtures (per-test isolation)
# =============================================================================


@pytest.fixture
def schema(postgis: PostGISFixture, request) -> Generator[str, None, None]:
    """
    Create an isolated database schema for a single test.

    The schema is automatically dropped after the test completes.
    """
    schema_name = postgis.create_isolated_schema(request.node.name)
    yield schema_name
    postgis.drop_schema(schema_name)


@pytest.fixture
def test_data(postgis: PostGISFixture, schema: str) -> TestDataBuilder:
    """Get a test data builder for the current test's schema."""
    return postgis.create_test_data(schema)


@pytest.fixture
def geometry_generator() -> GeometryGenerator:
    """Get a geometry generator for creating test geometries."""
    return GeometryGenerator()


@pytest.fixture
def http_client(base_url: str) -> Generator[httpx.Client, None, None]:
    """
    HTTP client configured for the Honua server.

    Includes common headers and timeout settings.
    """
    with httpx.Client(
        base_url=base_url,
        timeout=30.0,
        headers={
            "Accept": "application/json",
        },
    ) as client:
        yield client


@pytest.fixture
def geojson_client(base_url: str) -> Generator[httpx.Client, None, None]:
    """HTTP client configured to accept GeoJSON responses."""
    with httpx.Client(
        base_url=base_url,
        timeout=30.0,
        headers={
            "Accept": "application/geo+json",
        },
    ) as client:
        yield client


# =============================================================================
# Geometry fixtures for parameterized tests
# =============================================================================


@pytest.fixture(params=[
    "point",
    "multipoint",
    "linestring",
    "multilinestring",
    "polygon_simple",
    "polygon_with_hole",
    "polygon_with_multiple_holes",
    "multipolygon_simple",
    "multipolygon_with_holes",
    "geometry_collection",
])
def geometry_type(request, geometry_generator: GeometryGenerator) -> TestGeometry:
    """
    Parameterized fixture for all non-null geometry types.

    Tests using this fixture will run once for each geometry type.
    """
    method = getattr(geometry_generator, request.param)
    return method()


@pytest.fixture
def all_geometries(geometry_generator: GeometryGenerator) -> list[TestGeometry]:
    """Get all geometry types including null."""
    return geometry_generator.all_geometries()


@pytest.fixture
def point_grid(geometry_generator: GeometryGenerator) -> list[TestGeometry]:
    """Generate a 5x5 grid of points for pagination testing."""
    return geometry_generator.points_grid(rows=5, cols=5)


# =============================================================================
# Service/Layer fixtures for FeatureServer tests
# =============================================================================


@pytest.fixture
def test_service_id() -> str:
    """Default test service ID."""
    return "test_service"


@pytest.fixture
def test_layer_id() -> int:
    """Default test layer ID."""
    return 0


# =============================================================================
# OGC-specific fixtures
# =============================================================================


@pytest.fixture
def test_collection_id() -> str:
    """Default test collection ID for OGC tests."""
    return "0"  # Maps to layer 0


# =============================================================================
# Helper functions available to tests
# =============================================================================


def assert_geojson_feature_collection(response: httpx.Response):
    """Assert that a response contains a valid GeoJSON FeatureCollection."""
    assert response.status_code == 200
    data = response.json()
    assert data.get("type") == "FeatureCollection"
    assert "features" in data
    assert isinstance(data["features"], list)
    return data


def assert_geojson_feature(feature: dict):
    """Assert that a dict is a valid GeoJSON Feature."""
    assert feature.get("type") == "Feature"
    assert "geometry" in feature
    assert "properties" in feature
    return feature


def assert_esri_feature_set(response: httpx.Response):
    """Assert that a response contains a valid Esri FeatureSet."""
    assert response.status_code == 200
    data = response.json()
    assert "features" in data
    assert isinstance(data["features"], list)
    return data


def validate_geometry_with_shapely(geojson_geometry: dict | None, expected_type: str = None):
    """Validate a GeoJSON geometry using shapely."""
    from shapely.geometry import shape

    if geojson_geometry is None:
        return None

    geom = shape(geojson_geometry)
    assert geom.is_valid, f"Invalid geometry: {geom.wkt}"

    if expected_type:
        assert geom.geom_type == expected_type, f"Expected {expected_type}, got {geom.geom_type}"

    return geom
