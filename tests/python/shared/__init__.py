# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Shared test infrastructure for Honua integration tests.

This module provides:
- PostGIS container management via Testcontainers
- Honua server process management
- Geometry generators for comprehensive spatial testing
- HTTP client helpers with retry logic
"""

from .geometry import GeometryGenerator, ALL_GEOMETRY_TYPES
from .postgis import PostGISFixture, TestDataBuilder
from .server import HonuaServer

__all__ = [
    "GeometryGenerator",
    "ALL_GEOMETRY_TYPES",
    "PostGISFixture",
    "TestDataBuilder",
    "HonuaServer",
]
