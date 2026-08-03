#!/usr/bin/env python3
"""Verify checked-in raster semantics inside the pinned GDAL worker image."""

from __future__ import annotations

import argparse
import json
import math
import pathlib
import subprocess
import tempfile
from typing import Any

import numpy
from osgeo import gdal, osr


EXPECTED_GDAL_VERSION = "3.13.1"
FIXTURE_ID = "surface.slope-plane-degrees.v1"


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--fixtures",
        type=pathlib.Path,
        default=pathlib.Path(
            "tests/dotnet/Honua.TestKit/RasterSemantics/Fixtures/"
            "raster-semantic-fixtures.json"
        ),
    )
    return parser.parse_args()


def _load_fixture(path: pathlib.Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as stream:
        fixtures = json.load(stream)
    matches = [fixture for fixture in fixtures if fixture.get("id") == FIXTURE_ID]
    if len(matches) != 1:
        raise AssertionError(f"expected exactly one fixture {FIXTURE_ID!r}")
    return matches[0]


def _canonical_pixel_type(data_type: int) -> str:
    mapping = {
        gdal.GDT_Byte: "8BUI",
        gdal.GDT_Int16: "16BSI",
        gdal.GDT_UInt16: "16BUI",
        gdal.GDT_Int32: "32BSI",
        gdal.GDT_UInt32: "32BUI",
        gdal.GDT_Float32: "32BF",
        gdal.GDT_Float64: "64BF",
    }
    if data_type not in mapping:
        raise AssertionError(f"unsupported GDAL pixel type {gdal.GetDataTypeName(data_type)!r}")
    return mapping[data_type]


def _allowed(expected: float, actual: float, absolute: float, relative: float) -> float:
    return max(absolute, relative * max(abs(expected), abs(actual)))


def _assert_close(
    path: str,
    expected: float,
    actual: float,
    absolute: float,
    relative: float,
) -> None:
    if not math.isfinite(expected) or not math.isfinite(actual):
        raise AssertionError(f"{path}: non-finite semantic evidence")
    delta = abs(expected - actual)
    allowed = _allowed(expected, actual, absolute, relative)
    if delta > allowed:
        raise AssertionError(
            f"{path}: expected={expected!r} actual={actual!r} "
            f"delta={delta!r} allowed={allowed!r}"
        )


def _write_plane(path: pathlib.Path) -> None:
    driver = gdal.GetDriverByName("GTiff")
    dataset = driver.Create(str(path), 3, 3, 1, gdal.GDT_Float32)
    if dataset is None:
        raise AssertionError("GDAL could not create the semantic source raster")
    dataset.SetGeoTransform((500000, 1, 0, 2200000, 0, -1))
    spatial_reference = osr.SpatialReference()
    spatial_reference.ImportFromEPSG(32604)
    dataset.SetProjection(spatial_reference.ExportToWkt())
    band = dataset.GetRasterBand(1)
    band.SetNoDataValue(-9999)
    band.WriteArray(numpy.array([[0, 1, 2], [0, 1, 2], [0, 1, 2]], dtype=numpy.float32))
    band.FlushCache()
    dataset.FlushCache()
    dataset = None


def _run_slope(source: pathlib.Path, output: pathlib.Path) -> None:
    subprocess.run(
        [
            "gdaldem",
            "slope",
            "-of",
            "GTiff",
            "-q",
            "-s",
            "1",
            str(source),
            str(output),
        ],
        check=True,
        capture_output=True,
        text=True,
        timeout=60,
    )


def _assert_fixture(fixture: dict[str, Any], output: pathlib.Path) -> None:
    expected = fixture["expected"]
    expected_grid = expected["grid"]
    expected_band = expected["bands"][0]
    tolerance = fixture["tolerance"]
    dataset = gdal.Open(str(output), gdal.GA_ReadOnly)
    if dataset is None:
        raise AssertionError("GDAL could not reopen the semantic output")

    if dataset.RasterXSize != expected_grid["width"]:
        raise AssertionError("grid.width differs")
    if dataset.RasterYSize != expected_grid["height"]:
        raise AssertionError("grid.height differs")
    spatial_reference = osr.SpatialReference()
    spatial_reference.ImportFromWkt(dataset.GetProjection())
    spatial_reference.AutoIdentifyEPSG()
    srid = int(spatial_reference.GetAuthorityCode(None))
    if srid != expected_grid["srid"]:
        raise AssertionError(f"grid.srid: expected={expected_grid['srid']} actual={srid}")
    for index, (expected_value, actual_value) in enumerate(
        zip(expected_grid["transform"], dataset.GetGeoTransform(), strict=True)
    ):
        _assert_close(
            f"grid.transform[{index}]",
            float(expected_value),
            float(actual_value),
            float(tolerance["gridAbsolute"]),
            0,
        )

    band = dataset.GetRasterBand(1)
    pixel_type = _canonical_pixel_type(band.DataType)
    if pixel_type != expected_band["pixelType"]:
        raise AssertionError(
            f"bands[0].pixelType: expected={expected_band['pixelType']} actual={pixel_type}"
        )
    color = gdal.GetColorInterpretationName(band.GetColorInterpretation()).lower()
    if color != expected_band["colorInterpretation"]:
        raise AssertionError(
            "bands[0].colorInterpretation: "
            f"expected={expected_band['colorInterpretation']} actual={color}"
        )
    no_data = band.GetNoDataValue()
    if no_data != expected_band["noData"]:
        raise AssertionError(
            f"bands[0].noData: expected={expected_band['noData']} actual={no_data}"
        )

    actual_cells = band.ReadAsArray().reshape(-1).tolist()
    expected_cells = expected_band["cells"]
    if len(actual_cells) != len(expected_cells):
        raise AssertionError("bands[0].cells.count differs")
    for index, (expected_value, actual_value) in enumerate(
        zip(expected_cells, actual_cells, strict=True)
    ):
        actual_semantic = None if actual_value == no_data else float(actual_value)
        if expected_value is None or actual_semantic is None:
            if expected_value is not None or actual_semantic is not None:
                raise AssertionError(f"bands[0].cells[{index}]: NoData topology differs")
            continue
        _assert_close(
            f"bands[0].cells[{index}]",
            float(expected_value),
            actual_semantic,
            float(tolerance["cellAbsolute"]),
            float(tolerance["cellRelative"]),
        )


def main() -> int:
    args = _parse_args()
    runtime_version = gdal.VersionInfo("--version")
    if not runtime_version.startswith(f"GDAL {EXPECTED_GDAL_VERSION}"):
        raise AssertionError(
            f"semantic evidence requires GDAL {EXPECTED_GDAL_VERSION}; got {runtime_version!r}"
        )
    fixture = _load_fixture(args.fixtures)
    with tempfile.TemporaryDirectory(prefix="honua-raster-semantic-") as scratch:
        scratch_path = pathlib.Path(scratch)
        source = scratch_path / "plane.tif"
        output = scratch_path / "slope.tif"
        _write_plane(source)
        _run_slope(source, output)
        _assert_fixture(fixture, output)
    print(f"Raster semantic fixture {FIXTURE_ID} passed on {runtime_version}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
