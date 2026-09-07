# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""Collect the real OGC items suite alongside the unit conftest."""

from __future__ import annotations

import os
from pathlib import Path
import subprocess
import sys

import pytest


@pytest.mark.parametrize("unit_first", [True, False])
def test_ogc_items_collects_with_unit_conftest(unit_first: bool):
    python_root = Path(__file__).resolve().parents[1]
    paths = ["unit/test_shared_exports.py", "ogc_features/test_items.py"]
    if not unit_first:
        paths.reverse()
    result = subprocess.run(
        [sys.executable, "-m", "pytest", "--collect-only", "-q", "-o", "addopts=",
         "-p", "no:cacheprovider", *paths],
        cwd=python_root,
        env={**os.environ, "PYTHONIOENCODING": "utf-8"},
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=60,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert "ogc_features/test_items.py::TestItemsBasic::test_items_returns_200" in result.stdout
    assert "unit/test_shared_exports.py::test_shared_exports_are_declared_and_loaded_lazily" in result.stdout
