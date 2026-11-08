import importlib.util
from pathlib import Path
import tempfile
import unittest
from PIL import Image


class ServingFormatAssertionsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('serving_formats', Path(__file__).with_name('verify-serving-formats.py'))
        cls.formats = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.formats)

    def test_import_failure_with_http_success_is_rejected(self):
        with self.assertRaises(ValueError):
            self.formats.verify_import({'success': False, 'featureCount': 3}, 3)

    def test_partial_import_is_rejected(self):
        with self.assertRaises(ValueError):
            self.formats.verify_import({'success': True, 'featureCount': 2}, 3)

    def test_complete_import_is_accepted(self):
        self.formats.verify_import({'success': True, 'featureCount': 3}, 3)

    def legend(self, glyph_x):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / 'legend.png'
        image = Image.new('RGBA', (90, 32))
        image.putpixel((14, 14), (0, 0, 0, 255))
        if glyph_x is not None:
            image.putpixel((glyph_x, 14), (0, 0, 0, 255))
        image.save(path)
        return path

    def test_symbol_without_label_pixels_is_rejected(self):
        with self.assertRaises(ValueError):
            self.formats.verify_legend(self.legend(None))

    def test_pixels_in_the_label_region_are_accepted(self):
        self.formats.verify_legend(self.legend(50))


if __name__ == '__main__':
    unittest.main()
