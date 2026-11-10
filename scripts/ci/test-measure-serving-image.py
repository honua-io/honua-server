import gzip
import hashlib
import importlib.util
import io
import json
import tarfile
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch


class ServingImageMeasurementTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location(
            'measurement', Path(__file__).with_name('measure-serving-image.py'))
        cls.measurement = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.measurement)

    def make_oci_archive(self, directory):
        path = Path(directory) / 'image.tar'
        blobs = {}
        def blob(payload):
            digest = 'sha256:' + hashlib.sha256(payload).hexdigest()
            blobs['blobs/sha256/' + digest.split(':')[1]] = payload
            return {'digest': digest, 'size': len(payload)}
        config = blob(b'{"architecture":"amd64","os":"linux"}')
        layer = blob(gzip.compress(b'image layer bytes', mtime=0))
        layer['mediaType'] = 'application/vnd.oci.image.layer.v1.tar+gzip'
        manifest = blob(json.dumps({'schemaVersion': 2, 'config': config,
                                    'layers': [layer]}).encode())
        blobs['index.json'] = json.dumps({'manifests': [manifest]}).encode()
        with tarfile.open(path, 'w') as archive:
            for name, payload in blobs.items():
                member = tarfile.TarInfo(name)
                member.size = len(payload)
                archive.addfile(member, io.BytesIO(payload))
        return path, config['digest'], layer['size']

    def test_oci_compressed_bytes_are_bound_to_loaded_image(self):
        with tempfile.TemporaryDirectory() as directory:
            path, image_id, layer_size = self.make_oci_archive(directory)
            result = self.measurement.measure_oci_archive(path, image_id)
            self.assertEqual(result['compressed_layer_bytes'], layer_size)
            self.assertEqual(result['config_digest'], image_id)

    def test_oci_archive_for_different_image_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path, _, _ = self.make_oci_archive(directory)
            with self.assertRaisesRegex(ValueError, 'loaded image'):
                self.measurement.measure_oci_archive(path, 'sha256:' + '0' * 64)

    def test_registry_measurement_selects_loaded_platform_from_attested_index(self):
        image_id = 'sha256:' + 'a' * 64
        layer = {'size': 37, 'mediaType': 'application/vnd.oci.image.layer.v1.tar+gzip'}
        manifest = json.dumps({'config': {'digest': image_id, 'size': 42}, 'layers': [layer]}).encode()
        child_digest = 'sha256:' + hashlib.sha256(manifest).hexdigest()
        index = json.dumps({'manifests': [{'digest': child_digest, 'size': len(manifest)}]}).encode()
        root = 'registry.example/server@sha256:' + hashlib.sha256(index).hexdigest()
        with patch.object(self.measurement.subprocess, 'check_output', side_effect=[index, manifest]):
            result = self.measurement.measure_registry_image(root, image_id)
        self.assertEqual(result['compressed_layer_bytes'], 37)
        self.assertEqual(result['config_digest'], image_id)

    def test_registry_measurement_rejects_different_loaded_image(self):
        manifest = json.dumps({'config': {'digest': 'sha256:' + 'a' * 64, 'size': 42}, 'layers': []}).encode()
        root = 'registry.example/server@sha256:' + hashlib.sha256(manifest).hexdigest()
        with patch.object(self.measurement.subprocess, 'check_output', return_value=manifest):
            with self.assertRaisesRegex(ValueError, 'loaded image'):
                self.measurement.measure_registry_image(root, 'sha256:' + 'b' * 64)

    def test_registry_measurement_rejects_manifest_checksum_mismatch(self):
        with patch.object(self.measurement.subprocess, 'check_output', return_value=b'{}'):
            with self.assertRaisesRegex(ValueError, 'checksum'):
                self.measurement.measure_registry_image('registry.example/server@sha256:' + 'a' * 64,
                                                        'sha256:' + 'b' * 64)

    def test_compressed_measurement_rejects_empty_or_uncompressed_layers(self):
        image_id = 'sha256:' + 'a' * 64
        for layers in ([], [{'size': 37, 'mediaType': 'application/vnd.oci.image.layer.v1.tar'}]):
            with self.subTest(layers=layers):
                manifest = json.dumps({'config': {'digest': image_id, 'size': 42}, 'layers': layers}).encode()
                with self.assertRaisesRegex(ValueError, 'compressed layers'):
                    self.measurement.compressed_manifest_measurement(manifest, image_id)

    def test_default_image_accepts_exact_uncompressed_budget(self):
        self.measurement.verify_size(220_000_000, 'postgis')

    def test_default_image_rejects_uncompressed_budget_overrun(self):
        with self.assertRaisesRegex(ValueError, '220000000'):
            self.measurement.verify_size(220_000_001, 'postgis')

    def test_full_image_records_size_without_default_budget(self):
        self.measurement.verify_size(711_085_393, 'full')

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

    def test_default_image_rejects_provider_only_transitive_files(self):
        for name in ('Google.Cloud.Storage.V1.dll', 'libsf_mini_core.so'):
            with self.subTest(file=name):
                files = {name: 1 for name in self.measurement.FORMAT_FILES}
                files['app/' + name] = 20
                with self.assertRaises(ValueError):
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


    def test_native_aot_requires_compiler_response_evidence(self):
        evidence = {'assets': {'targets': {}}, 'responses': [], 'compilation': 'aot'}
        with self.assertRaisesRegex(ValueError, 'compiler'):
            self.measurement.verify_build_inputs(evidence, 'postgis')

    def test_native_aot_rejects_excluded_assembly_in_compiler_inputs(self):
        evidence = {'assets': {'targets': {}}, 'responses': ['Honua.Server.dll\n-r:/app/Honua.DuckDB.dll'],
                    'compilation': 'aot'}
        with self.assertRaisesRegex(ValueError, 'Honua.DuckDB.dll'):
            self.measurement.verify_build_inputs(evidence, 'postgis')

    def test_jit_rejects_excluded_assembly_in_runtime_manifest(self):
        evidence = {'assets': {'targets': {}}, 'responses': [], 'compilation': 'jit',
                    'deps': {'targets': {'net10.0': {'optional/1': {'runtime': {'Honua.MySql.dll': {}}}}}}}
        with self.assertRaisesRegex(ValueError, 'Honua.MySql.dll'):
            self.measurement.verify_build_inputs(evidence, 'postgis')

    def test_native_aot_accepts_a_store_free_compiler_closure(self):
        evidence = {'assets': {'targets': {}}, 'responses': ['Honua.Server.dll\n-r:/app/Honua.Postgres.dll'],
                    'compilation': 'aot'}
        self.measurement.verify_build_inputs(evidence, 'postgis')


if __name__ == '__main__':
    unittest.main()
