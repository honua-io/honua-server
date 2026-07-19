# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Tests for the shared test-infrastructure package exports."""

from __future__ import annotations

import sys


EXPECTED_EXPORTS = {
    "GeometryGenerator",
    "ALL_GEOMETRY_TYPES",
    "PostGISFixture",
    "TestDataBuilder",
    "SeedRunner",
    "HonuaServer",
}


def test_shared_exports_are_declared_and_loaded_lazily():
    sys.modules.pop("shared.geometry", None)
    sys.modules.pop("shared", None)

    import shared

    assert set(shared.__all__) == EXPECTED_EXPORTS
    assert "shared.geometry" not in sys.modules

    assert shared.GeometryGenerator is not None
    assert shared.ALL_GEOMETRY_TYPES is not None
    assert "shared.geometry" in sys.modules
