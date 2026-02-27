# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Shared test infrastructure for Honua integration tests.

Exports are loaded lazily to avoid importing optional heavy dependencies
unless they are explicitly requested by a test.
"""

from __future__ import annotations

__all__ = [
    "GeometryGenerator",
    "ALL_GEOMETRY_TYPES",
    "PostGISFixture",
    "TestDataBuilder",
    "SeedRunner",
    "HonuaServer",
]


def __getattr__(name: str):
    if name in {"GeometryGenerator", "ALL_GEOMETRY_TYPES"}:
        from .geometry import ALL_GEOMETRY_TYPES, GeometryGenerator

        return {
            "GeometryGenerator": GeometryGenerator,
            "ALL_GEOMETRY_TYPES": ALL_GEOMETRY_TYPES,
        }[name]

    if name in {"PostGISFixture", "TestDataBuilder"}:
        from .postgis import PostGISFixture, TestDataBuilder

        return {
            "PostGISFixture": PostGISFixture,
            "TestDataBuilder": TestDataBuilder,
        }[name]

    if name == "SeedRunner":
        from .seed import SeedRunner

        return SeedRunner

    if name == "HonuaServer":
        from .server import HonuaServer

        return HonuaServer

    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")
