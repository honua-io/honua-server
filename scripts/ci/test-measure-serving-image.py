import importlib.util
from pathlib import Path
import unittest


class ServingImageMeasurementTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location(
            'measurement', Path(__file__).with_name('measure-serving-image.py'))
        cls.measurement = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.measurement)

    def test_default_image_rejects_excluded_store_assembly_in_subdirectory(self):
        files = {name: 1 for name in self.measurement.FORMAT_FILES}
        files['app/providers/Honua.DuckDB.dll'] = 20
        with self.assertRaisesRegex(ValueError, 'Honua.DuckDB.dll'):
            self.measurement.verify_files(files, 'postgis')

    def test_default_image_rejects_excluded_native(self):
        files = {name: 1 for name in self.measurement.FORMAT_FILES}
        files['app/libduckdb.so'] = 20
        with self.assertRaisesRegex(ValueError, 'libduckdb.so'):
            self.measurement.verify_files(files, 'postgis')

    def test_default_image_keeps_file_format_libraries(self):
        files = {name: 1 for name in self.measurement.FORMAT_FILES}
        self.measurement.verify_files(files, 'postgis')
        del files['app/libe_sqlite3.so']
        with self.assertRaisesRegex(ValueError, 'libe_sqlite3.so'):
            self.measurement.verify_files(files, 'postgis')

    def test_full_image_requires_all_store_assemblies(self):
        files = {name: 1 for name in self.measurement.FORMAT_FILES}
        files.update({'app/' + name: 1 for name in self.measurement.STORE_FILES})
        self.measurement.verify_files(files, 'full')
        del files['app/Honua.MySql.dll']
        with self.assertRaisesRegex(ValueError, 'Honua.MySql.dll'):
            self.measurement.verify_files(files, 'full')

    def test_image_rejects_a_runtime_shell(self):
        files = {name: 1 for name in self.measurement.FORMAT_FILES}
        files['usr/bin/dash'] = 30
        with self.assertRaisesRegex(ValueError, 'shell'):
            self.measurement.verify_files(files, 'postgis')

    def test_image_rejects_satellite_assemblies(self):
        files = {name: 1 for name in self.measurement.FORMAT_FILES}
        files['app/fr/Microsoft.Data.SqlClient.resources.dll'] = 12
        with self.assertRaisesRegex(ValueError, 'satellite'):
            self.measurement.verify_files(files, 'postgis')


if __name__ == '__main__':
    unittest.main()
