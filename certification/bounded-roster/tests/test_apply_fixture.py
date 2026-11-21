"""Unit tests for the bounded-roster cloud raster fixture author."""
from __future__ import annotations

import importlib
import sys
import types
import unittest
from pathlib import Path
from unittest import mock

FIXTURE_DIR = str(Path(__file__).resolve().parents[1] / "fixture")


class _FakeBand:
    def SetNoDataValue(self, _value: float) -> None:
        return

    def WriteRaster(self, *_args) -> None:
        return


class _FakeDataset:
    RasterXSize = 256
    RasterYSize = 256

    def SetGeoTransform(self, _gt: list[float]) -> None:
        return

    def SetProjection(self, _wkt: str) -> None:
        return

    def GetRasterBand(self, _index: int) -> _FakeBand:
        return _FakeBand()

    def GetGeoTransform(self) -> tuple[float, ...]:
        return (0.0, 1.0, 0.0, 0.0, 0.0, -1.0)

    def GetMetadata(self, _domain: str) -> dict[str, str]:
        return {"LAYOUT": "COG"}


class _FakeSpatialReference:
    def ImportFromEPSG(self, _epsg: int) -> None:
        return

    def ExportToWkt(self) -> str:
        return "EPSG:4326"


def _build_fake_osgeo(captured: dict[str, object]) -> dict[str, types.ModuleType]:
    fake_gdal = types.ModuleType("osgeo.gdal")
    fake_gdal.GDT_Byte = 1
    fake_gdal.UseExceptions = lambda: None
    fake_gdal.SetConfigOption = lambda _k, _v: None
    fake_gdal.GetDriverByName = lambda _name: mock.Mock(Create=lambda *_a, **_kw: _FakeDataset())
    fake_gdal.Translate = lambda target, _source, **kwargs: captured.update(target=target, kwargs=kwargs)
    fake_gdal.Open = lambda _path: _FakeDataset()
    fake_gdal.VSIFOpenL = lambda _path, _mode: object()
    fake_gdal.VSIFReadL = lambda _size, _count, _handle: b""
    fake_gdal.VSIFCloseL = lambda _handle: None

    fake_osr = types.ModuleType("osgeo.osr")
    fake_osr.SpatialReference = _FakeSpatialReference

    fake_osgeo = types.ModuleType("osgeo")
    fake_osgeo.gdal = fake_gdal
    fake_osgeo.osr = fake_osr
    return {"osgeo": fake_osgeo, "osgeo.gdal": fake_gdal, "osgeo.osr": fake_osr}


class ApplyFixtureTests(unittest.TestCase):
    def test_author_cog_uses_nearest_resampling(self) -> None:
        captured: dict[str, object] = {}
        sys.path.insert(0, FIXTURE_DIR)
        try:
            with mock.patch.dict(sys.modules, _build_fake_osgeo(captured)):
                sys.modules.pop("apply_fixture", None)
                apply_fixture = importlib.import_module("apply_fixture")
                result = apply_fixture.author_cog()
        finally:
            sys.path.remove(FIXTURE_DIR)
            sys.modules.pop("apply_fixture", None)

        self.assertEqual(result["key"], "cog/roster-3857.tif")
        kwargs = captured["kwargs"]
        self.assertEqual(kwargs["format"], "COG")
        self.assertIn("RESAMPLING=NEAREST", kwargs["creationOptions"])
        self.assertIn("TILING_SCHEME=GoogleMapsCompatible", kwargs["creationOptions"])
        self.assertIn("COMPRESS=DEFLATE", kwargs["creationOptions"])
