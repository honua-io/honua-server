"""Challenge the fixture's value, orientation and nodata oracle with real PNGs."""
import importlib.util
from io import BytesIO
from pathlib import Path
import unittest
import tempfile

import zarr

import numpy as np
from PIL import Image

SPEC = importlib.util.spec_from_file_location(
    "multidim_fixture", Path(__file__).with_name("verify.py"))
fixture = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(fixture)


class MultidimFixtureOracleTests(unittest.TestCase):
    def png(self, values, missing):
        pixels = np.zeros((4, 4, 4), dtype=np.uint8)
        for y, row in enumerate(values):
            for x, value in enumerate(row):
                if (y, x) != missing:
                    gray = round(value * 255 / 40)
                    pixels[y, x] = [gray, gray, gray, 255]
        output = BytesIO()
        Image.fromarray(pixels).save(output, format="PNG")
        return output.getvalue()

    def test_zarr_directory_markers_and_independent_values(self):
        # Author real Zarr bytes independently of verify.expected_temperature.
        with tempfile.TemporaryDirectory() as directory:
            group = zarr.open_group(directory, mode="w")
            values = np.array([
                [[-9999, 11, 12, 13], [14, 15, 16, 17], [18, 19, 20, 21], [22, 23, 24, 25]],
                [[35, 34, 33, 32], [31, 30, 29, 28], [27, 26, 25, 24], [23, 22, 21, -9999]],
            ], dtype="float32")
            temperature = group.create_dataset("sea_surface_temperature", data=values, fill_value=-9999)
            temperature.attrs.update(units="degC", _ARRAY_DIMENSIONS=["time", "latitude", "longitude"])
            group.create_dataset("time", data=[0, 24]).attrs["units"] = "hours since 2024-01-01 00:00:00"
            group.create_dataset("latitude", data=[37.70, 37.75, 37.80, 37.85])
            group.create_dataset("longitude", data=[-122.50, -122.45, -122.40, -122.35])
            prefix = "imageserver/sea-surface-temperature.zarr/"
            objects = {prefix + str(path.relative_to(directory)): path.read_bytes()
                       for path in Path(directory).rglob("*") if path.is_file()}
            objects[prefix] = b""
            objects[prefix + "sea_surface_temperature/"] = b""

            class ObjectStore:
                def head_object(self, *, Bucket, Key):
                    return {"ContentLength": len(objects[Key])}

                def download_file(self, bucket, key, target):
                    Path(target).write_bytes(objects[key])

            fixture.verify_zarr(ObjectStore(), set(objects))
            objects[prefix] = b"unexpected nonempty marker"
            with self.assertRaises(AssertionError):
                fixture.verify_zarr(ObjectStore(), set(objects))
            objects[prefix] = b""
            # A valid store with one wrong ordinate must still fail.
            group["latitude"][0] = 37.71
            for path in (Path(directory) / "latitude").rglob("*"):
                if path.is_file():
                    objects[prefix + str(path.relative_to(directory))] = path.read_bytes()
            with self.assertRaises(AssertionError):
                fixture.verify_zarr(ObjectStore(), set(objects))

    def test_exact_values_and_north_up_nodata_pass(self):
        fixture.verify_pixels(self.png(
            [[22, 23, 24, 25], [18, 19, 20, 21], [14, 15, 16, 17], [10, 11, 12, 13]],
            (3, 0)), 0)
        fixture.verify_pixels(self.png(
            [[23, 22, 21, 20], [27, 26, 25, 24], [31, 30, 29, 28], [35, 34, 33, 32]],
            (0, 3)), 1)

    def test_ignored_time_selection_is_rejected(self):
        with self.assertRaises(AssertionError):
            fixture.verify_pixels(self.png(
                [[22, 23, 24, 25], [18, 19, 20, 21], [14, 15, 16, 17], [10, 11, 12, 13]],
                (3, 0)), 1)

    def test_lost_nodata_is_rejected(self):
        with self.assertRaises(AssertionError):
            fixture.verify_pixels(self.png(
                [[22, 23, 24, 25], [18, 19, 20, 21], [14, 15, 16, 17], [10, 11, 12, 13]],
                None), 0)

    def test_swapped_north_south_is_rejected(self):
        with self.assertRaises(AssertionError):
            fixture.verify_pixels(self.png(
                [[10, 11, 12, 13], [14, 15, 16, 17], [18, 19, 20, 21], [22, 23, 24, 25]],
                (0, 0)), 0)


if __name__ == "__main__":
    unittest.main()
