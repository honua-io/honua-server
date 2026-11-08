import importlib.util
from pathlib import Path
import tempfile
import unittest
import sqlite3
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

    def legend(self, glyph_x, background=(0, 0, 0, 0)):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / 'legend.png'
        image = Image.new('RGBA', (90, 32), background)
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

    def test_opaque_background_without_label_pixels_is_rejected(self):
        with self.assertRaises(ValueError):
            self.formats.verify_legend(self.legend(None, (255, 255, 255, 255)))

    def test_opaque_background_with_label_pixels_is_accepted(self):
        self.formats.verify_legend(self.legend(50, (255, 255, 255, 255)))

    def geopackage(self, feature_count):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / 'export.gpkg'
        with sqlite3.connect(path) as database:
            database.execute('CREATE TABLE gpkg_contents(table_name TEXT, data_type TEXT)')
            database.execute("INSERT INTO gpkg_contents VALUES ('points', 'features')")
            database.execute('CREATE TABLE points(id INTEGER)')
            database.executemany('INSERT INTO points VALUES (?)', [(value,) for value in range(feature_count)])
        return path

    def test_empty_feature_table_is_rejected(self):
        with self.assertRaises(ValueError):
            self.formats.verify_geopackage(self.geopackage(0))

    def test_partial_feature_table_is_rejected(self):
        with self.assertRaises(ValueError):
            self.formats.verify_geopackage(self.geopackage(2))

    def test_complete_feature_table_is_accepted(self):
        self.formats.verify_geopackage(self.geopackage(3))


if __name__ == '__main__':
    unittest.main()
