"""Exact canonical NetCDF acceptance, independent of worker qualification."""
from __future__ import annotations

import importlib.util
from pathlib import Path
import tempfile
import shutil
import unittest
import unittest.mock
from argparse import Namespace

try:
    import h5py
    import numpy as np
    import xarray as xr
    import h5netcdf  # noqa: F401 - xarray engine required by these fixture tests
except ImportError:
    h5py = np = xr = None

SPEC = importlib.util.spec_from_file_location(
    "canonical_validator", Path(__file__).with_name("validate-canonical-artifacts.py"))
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


@unittest.skipIf(h5py is None, "NetCDF fixture checks require h5py, numpy, xarray and h5netcdf")
class NetcdfFidelityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "canonical.nc"
        self.dataset = xr.Dataset(
            {"temperature": (("time", "y", "x"), np.arange(512, dtype=np.float32).reshape(4, 8, 16),
                             {"units": "degC", "standard_name": "sea_surface_temperature"})},
            coords={"time": np.array(["2026-01-01", "2026-01-02", "2026-01-03", "2026-01-04"], dtype="datetime64[ns]"),
                    "y": ("y", np.linspace(22., 18., 8), {"standard_name": "latitude", "units": "degrees_north", "axis": "Y"}),
                    "x": ("x", np.linspace(-160., -156., 16), {"standard_name": "longitude", "units": "degrees_east", "axis": "X"})},
            attrs={"Conventions": "CF-1.10", "title": "Honua canonical multidimensional fixture"})
        self.write()

    def write(self, chunks=(1, 4, 4), dtype="float32"):
        self.dataset.to_netcdf(self.path, engine="h5netcdf", encoding={"temperature": {"chunksizes": chunks, "dtype": dtype}})

    def validate(self, clients=("h5py", "xarray")):
        def collect(observations, format_name, operation, client, lane, args, check, **kwargs):
            if client in clients:
                check()
        with unittest.mock.patch.object(MODULE, "_collect_client", side_effect=collect):
            MODULE.validate_hdf5_netcdf(self.path, Namespace())

    def test_exact_fixture_passes(self):
        self.validate()

    def test_value_corruption_is_rejected(self):
        with h5py.File(self.path, "r+") as f:
            f["temperature"][2, 3, 4] = -999
        with self.assertRaisesRegex(ValueError, "values"):
            self.validate()

    def test_wrong_chunks_are_rejected(self):
        self.write(chunks=(2, 4, 4))
        with self.assertRaisesRegex(ValueError, "chunks"):
            self.validate()

    def test_wrong_dtype_is_rejected(self):
        self.write(dtype="float64")
        with self.assertRaisesRegex(ValueError, "dtype"):
            self.validate()

    def test_wrong_shape_is_rejected(self):
        self.dataset = self.dataset.isel(time=slice(0, 3))
        self.write()
        with self.assertRaisesRegex(ValueError, "shape"):
            self.validate()

    def test_coordinate_corruption_is_rejected(self):
        for axis in ("time", "y", "x"):
            with self.subTest(axis=axis):
                self.write()
                with h5py.File(self.path, "r+") as f:
                    f[axis][0] += 1
                with self.assertRaisesRegex(ValueError, "coordinate"):
                    self.validate()

    def test_cf_metadata_corruption_is_rejected(self):
        for target, key in ((None, "Conventions"), ("temperature", "units"), ("temperature", "standard_name"),
                            ("x", "axis"), ("y", "units"), ("time", "calendar")):
            with self.subTest(target=target, key=key):
                self.write()
                with h5py.File(self.path, "r+") as f:
                    (f if target is None else f[target]).attrs[key] = "corrupt"
                with self.assertRaises(ValueError):
                    self.validate()

    def test_xarray_independently_rejects_corrupted_values(self):
        with h5py.File(self.path, "r+") as handle:
            handle["temperature"][1, 2, 3] = -321
        with self.assertRaisesRegex(ValueError, "values"):
            self.validate(clients=("xarray",))

    def test_xarray_independently_rejects_corrupted_coordinates(self):
        for axis in ("time", "y", "x"):
            with self.subTest(axis=axis):
                self.write()
                with h5py.File(self.path, "r+") as handle:
                    handle[axis][0] += 1
                with self.assertRaisesRegex(ValueError, "coordinate"):
                    self.validate(clients=("xarray",))

    def test_xarray_independently_rejects_corrupted_cf_metadata(self):
        for target, key in ((None, "Conventions"), ("temperature", "units"), ("temperature", "standard_name"),
                            ("x", "axis"), ("y", "units")):
            with self.subTest(target=target, key=key):
                self.write()
                with h5py.File(self.path, "r+") as handle:
                    (handle if target is None else handle[target]).attrs[key] = "corrupt"
                with self.assertRaisesRegex(ValueError, "CF metadata"):
                    self.validate(clients=("xarray",))

    def test_repack_corrupted_output_is_rejected_and_removed(self):
        for mutation in ("values", "chunks", "PAGE"):
            with self.subTest(mutation=mutation):
                outputs = []
                def run(tool, *arguments):
                    self.assertEqual(tool, "h5repack")
                    self.assertEqual(arguments[:4], ("-S", "PAGE", "-G", "262144"))
                    source, destination = arguments[-2:]
                    outputs.append(Path(destination))
                    if mutation == "chunks":
                        self.dataset.to_netcdf(destination, engine="h5netcdf",
                            encoding={"temperature": {"chunksizes": (2, 4, 4), "dtype": "float32"}})
                    else:
                        shutil.copyfile(source, destination)
                        if mutation == "values":
                            with h5py.File(destination, "r+") as handle:
                                handle["temperature"][0, 0, 0] = -123
                def collect(observations, format_name, operation, client, lane, args, check, **kwargs):
                    if client == "h5repack":
                        check()
                with unittest.mock.patch.object(MODULE, "_run", side_effect=run), \
                        unittest.mock.patch.object(MODULE, "_collect_client", side_effect=collect):
                    with self.assertRaisesRegex(ValueError, mutation):
                        MODULE.validate_hdf5_netcdf(self.path, Namespace())
                self.assertEqual(len(outputs), 1)
                self.assertFalse(outputs[0].exists(), "repack temporary output must be removed on failure")

    def test_dimension_order_corruption_is_rejected(self):
        with h5py.File(self.path, "r+") as f:
            f["temperature"].dims[0].detach_scale(f["time"])
            f["temperature"].dims[0].attach_scale(f["x"])
        with self.assertRaises(ValueError):
            self.validate()


if __name__ == "__main__":
    unittest.main()
