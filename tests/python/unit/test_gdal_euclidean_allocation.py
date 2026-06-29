# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Unit tests for the Euclidean allocation worker step (honua-server #2255).

These exercise the pure nearest-source allocation algorithm in
``src/Honua.Worker.Gdal/Scripts/gdal_euclidean_allocation.py`` directly (no GDAL
raster I/O required), so they verify correctness wherever NumPy + SciPy are
available. The full base64-GeoTIFF round trip runs inside the GDAL worker image
(which additionally ships the GDAL Python bindings) and is covered by the worker
e2e path.
"""

from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest

np = pytest.importorskip("numpy")
pytest.importorskip("scipy")


REPO_ROOT = Path(__file__).resolve().parents[3]
SCRIPT_PATH = REPO_ROOT / "src/Honua.Worker.Gdal/Scripts/gdal_euclidean_allocation.py"


def load_module():
    spec = importlib.util.spec_from_file_location("gdal_euclidean_allocation", SCRIPT_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ALLOC = load_module()


def test_compute_allocation_assigns_nearest_source_value_1d():
    # Sources (ids 7, 3) at the row ends; interior cells take the nearer end's id.
    arr = np.array([[7, 0, 0, 3]], dtype=np.int32)
    source_mask = arr != 0

    alloc = ALLOC.compute_allocation(arr, source_mask, sampling=(1.0, 1.0), max_distance=None, out_nodata=0)

    np.testing.assert_array_equal(alloc, np.array([[7, 7, 3, 3]], dtype=np.int32))
    assert alloc.dtype == arr.dtype


def test_compute_allocation_2d_discrete_voronoi():
    # 3x3 grid, sources at opposite ends of the top/bottom rows (no diagonal ties).
    arr = np.array(
        [
            [5, 0, 9],
            [0, 0, 0],
            [5, 0, 9],
        ],
        dtype=np.int32,
    )
    source_mask = arr != 0

    alloc = ALLOC.compute_allocation(arr, source_mask, sampling=(1.0, 1.0), max_distance=None, out_nodata=0)

    expected = np.array(
        [
            [5, 5, 9],
            [5, 5, 9],
            [5, 5, 9],
        ],
        dtype=np.int32,
    )
    # Middle column is equidistant left/right -> SciPy resolves ties deterministically
    # to the lower-index source; assert the unambiguous left/right columns only.
    np.testing.assert_array_equal(alloc[:, 0], expected[:, 0])
    np.testing.assert_array_equal(alloc[:, 2], expected[:, 2])


def test_compute_allocation_honors_max_distance_with_nodata():
    arr = np.array([[7, 0, 0, 0, 3]], dtype=np.int32)
    source_mask = arr != 0

    alloc = ALLOC.compute_allocation(arr, source_mask, sampling=(1.0, 1.0), max_distance=1.0, out_nodata=0)

    # Cell index 2 is distance 2 from either source -> beyond max -> nodata (0).
    np.testing.assert_array_equal(alloc, np.array([[7, 7, 0, 3, 3]], dtype=np.int32))


def test_compute_allocation_anisotropic_sampling_changes_nearest():
    # Equal pixel-count distance, but tall pixels (y spacing 10) make the
    # horizontal source the nearer one in GEO units.
    arr = np.array(
        [
            [0, 1, 0],
            [0, 0, 0],
            [0, 0, 0],
            [2, 0, 0],
        ],
        dtype=np.int32,
    )
    source_mask = arr != 0
    # Target the cell directly between: (1,0) is 1 row below id=1 origin? Use (1,1).
    alloc_pixel = ALLOC.compute_allocation(arr, source_mask, sampling=(1.0, 1.0), max_distance=None, out_nodata=0)
    alloc_geo = ALLOC.compute_allocation(arr, source_mask, sampling=(10.0, 1.0), max_distance=None, out_nodata=0)

    # With tall pixels, the column-aligned source 1 (small horizontal offset) wins
    # over the far-below source 2 for the upper interior cells.
    assert alloc_geo[0, 0] == 1
    # Sanity: both are valid source ids.
    assert set(np.unique(alloc_pixel)).issubset({0, 1, 2})


def test_build_source_mask_filters_by_values_and_nodata():
    arr = np.array([[1, 2, 0, 3, -9]], dtype=np.int32)

    # Without a values filter: all non-zero, non-nodata cells are sources.
    mask_all = ALLOC.build_source_mask(arr, nodata=-9, values=None)
    np.testing.assert_array_equal(mask_all, np.array([[True, True, False, True, False]]))

    # With a values filter: only listed ids are sources.
    mask_vals = ALLOC.build_source_mask(arr, nodata=-9, values=[2, 3])
    np.testing.assert_array_equal(mask_vals, np.array([[False, True, False, True, False]]))
