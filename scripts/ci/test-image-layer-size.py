from dataclasses import dataclass
import gzip
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch


@dataclass(frozen=True)
class ArchiveOptions:
    archive_format: str = 'docker'
    layer_content: str = 'valid'
    compression: str = 'gzip'


class ImageLayerSizeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location(
            'measurement', Path(__file__).with_name('measure-serving-image.py'))
        cls.measurement = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.measurement)

    def archive(self, directory, options=ArchiveOptions()):
        layer = b'uncompressed layer bytes' * 100
        diff_id = 'sha256:' + hashlib.sha256(layer).hexdigest()
        config = json.dumps({'rootfs': {'type': 'layers', 'diff_ids': [diff_id]}}).encode()
        image_id = 'sha256:' + hashlib.sha256(config).hexdigest()
        contents = {}
        if options.archive_format == 'oci':
            def blob(payload):
                digest = 'sha256:' + hashlib.sha256(payload).hexdigest()
                contents['blobs/sha256/' + digest.split(':')[1]] = payload
                return {'digest': digest, 'size': len(payload)}
            config_descriptor = blob(config)
            layer_payload = b'wrong' if options.layer_content == 'corrupt' else layer
            layer_descriptor = blob(gzip.compress(layer_payload, mtime=0)
                                    if options.compression == 'gzip' else layer_payload)
            layer_descriptor['mediaType'] = 'application/vnd.oci.image.layer.v1.tar'
            if options.compression == 'gzip':
                layer_descriptor['mediaType'] += '+gzip'
            manifest = blob(json.dumps({'config': config_descriptor,
                                        'layers': [layer_descriptor]}).encode())
            contents['index.json'] = json.dumps({'manifests': [manifest]}).encode()
        else:
            contents['config.json'] = config
            contents['layer.tar'] = b'wrong' if options.layer_content == 'corrupt' else layer
            contents['manifest.json'] = json.dumps([
                {'Config': 'config.json', 'Layers': ['layer.tar']}]).encode()
        path = Path(directory) / 'image.tar'
        with tarfile.open(path, 'w') as archive:
            for name, payload in contents.items():
                member = tarfile.TarInfo(name)
                member.size = len(payload)
                archive.addfile(member, io.BytesIO(payload))
        return path, image_id, len(layer)

    def test_saved_image_counts_uncompressed_layers_for_both_archive_formats(self):
        for archive_format in ('docker', 'oci'):
            with self.subTest(archive_format=archive_format), tempfile.TemporaryDirectory() as directory:
                path, image_id, expected = self.archive(directory, ArchiveOptions(archive_format))
                self.assertEqual(self.measurement.saved_image_bytes(path, image_id), expected)

    def test_saved_image_rejects_layer_not_matching_config_diff_id(self):
        for archive_format in ('docker', 'oci'):
            with self.subTest(archive_format=archive_format), tempfile.TemporaryDirectory() as directory:
                path, image_id, _ = self.archive(directory, ArchiveOptions(archive_format, 'corrupt'))
                with self.assertRaisesRegex(ValueError, 'diff ID'):
                    self.measurement.saved_image_bytes(path, image_id)

    def test_saved_image_rejects_different_selected_image(self):
        for archive_format in ('docker', 'oci'):
            with self.subTest(archive_format=archive_format), tempfile.TemporaryDirectory() as directory:
                path, _, _ = self.archive(directory, ArchiveOptions(archive_format))
                with self.assertRaisesRegex(ValueError, 'loaded image'):
                    self.measurement.saved_image_bytes(path, 'sha256:' + '0' * 64)

    def test_saved_oci_uncompressed_layers_use_the_same_measurement(self):
        with tempfile.TemporaryDirectory() as directory:
            path, image_id, expected = self.archive(directory, ArchiveOptions('oci', compression='none'))
            self.assertEqual(self.measurement.saved_image_bytes(path, image_id), expected)

    def test_config_requires_one_diff_id_per_layer(self):
        for rootfs in ({}, {'type': 'layers', 'diff_ids': []},
                       {'type': 'layers', 'diff_ids': ['sha256:' + 'a' * 64] * 2}):
            with self.subTest(rootfs=rootfs), self.assertRaisesRegex(ValueError, 'diff IDs'):
                self.measurement.image_diff_ids({'rootfs': rootfs}, 1)

    def test_export_budget_and_savings_use_verified_bytes_for_both_images(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'project.assets.json').write_text('{"targets": {}}')
            args = type('Args', (), {'image': 'image', 'baseline': 'baseline', 'profile': 'postgis',
                                    'release_stage': 'rc', 'output': root / 'report.json',
                                    'default_image': True, 'compilation': 'jit',
                                    'oci_archive': None, 'evidence_directory': root})()
            files = {name: 1 for name in self.measurement.FORMAT_FILES}
            with patch.object(self.measurement, 'inspect_image',
                              return_value={'Id': 'baseline-id', 'Size': 600_000_000}), \
                 patch.object(self.measurement, 'measure_saved_image',
                              side_effect=[400_000_000, 370_000_000]), \
                 patch.object(self.measurement, 'exported_files',
                              return_value=(files, {'targets': {}})):
                self.measurement.measure_export(args, {'Id': 'selected', 'Size': 523_487_627,
                                                      'Config': {}})
            report = json.loads(args.output.read_text())
            self.assertEqual(report['bytes'], 370_000_000)
            self.assertEqual(report['baseline_bytes'], 400_000_000)
            self.assertEqual(report['saved_bytes'], 30_000_000)

    def test_published_budget_uses_layer_bytes_instead_of_storage_counter(self):
        for storage_size in (370_000_000, 523_487_627):
            with self.subTest(storage_size=storage_size), tempfile.TemporaryDirectory() as directory:
                output = Path(directory) / 'report.json'
                args = type('Args', (), {'image': 'immutable-image', 'profile': 'postgis',
                                        'release_stage': 'rc', 'output': output,
                                        'default_image': True, 'verify_jit_boundary': False})()
                with patch.object(self.measurement, 'measure_registry_image',
                                  return_value={'compressed_layer_bytes': 50}), \
                     patch.object(self.measurement, 'measure_saved_image', return_value=370_000_000):
                    self.measurement.measure_published(args, {'Id': 'selected', 'Size': storage_size})
                report = json.loads(output.read_text())
                self.assertEqual(report['bytes'], 370_000_000)
                self.assertEqual(report['docker_storage_bytes'], storage_size)
                self.assertEqual(report['size_method'], 'verified-uncompressed-layer-bytes')

    def test_published_budget_rejects_large_layers_even_with_small_storage_counter(self):
        with tempfile.TemporaryDirectory() as directory:
            args = type('Args', (), {'image': 'immutable-image', 'profile': 'postgis',
                                    'release_stage': 'rc', 'output': Path(directory) / 'report.json',
                                    'default_image': True, 'verify_jit_boundary': False})()
            with patch.object(self.measurement, 'measure_registry_image',
                              return_value={'compressed_layer_bytes': 50}), \
                 patch.object(self.measurement, 'measure_saved_image', return_value=370_000_001), \
                 self.assertRaisesRegex(ValueError, 'exceeds 370000000'):
                self.measurement.measure_published(args, {'Id': 'selected', 'Size': 200_000_000})


if __name__ == '__main__':
    unittest.main()
