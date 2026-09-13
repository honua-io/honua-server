"""Challenge the fixture's value, orientation and nodata oracle with real PNGs."""
import importlib.util
from io import BytesIO
from pathlib import Path
import unittest

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
