"""A successful header read is not evidence that feature payloads decoded."""
import copy
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import flatgeobuf_readback as reader


class FlatGeobufReadbackTests(unittest.TestCase):
    def payload(self):
        return {'layers': [{'featureCount': 2, 'features': [
            {'type': 'Feature', 'geometry': {'type': 'Point', 'coordinates': [1, 2]}, 'properties': {}},
            {'type': 'Feature', 'geometry': {'type': 'Point', 'coordinates': [3, 4]}, 'properties': {}},
        ]}]}

    def test_summary_and_incomplete_or_null_payloads_fail(self):
        valid = self.payload()
        cases = []
        summary = copy.deepcopy(valid)
        del summary['layers'][0]['features']
        cases.append(summary)
        truncated = copy.deepcopy(valid)
        truncated['layers'][0]['features'].pop()
        cases.append(truncated)
        null = copy.deepcopy(valid)
        null['layers'][0]['features'][0]['geometry'] = None
        cases.append(null)
        for payload in cases:
            with self.subTest(payload=payload), self.assertRaises(ValueError):
                reader.require_decoded_features(payload)
        self.assertEqual(2, reader.require_decoded_features(valid))

    def test_requests_full_decode_and_buffer_verification(self):
        result = subprocess.CompletedProcess([], 0, json.dumps(self.payload()), '')
        with patch.object(reader.subprocess, 'run', return_value=result) as run:
            self.assertEqual(self.payload(), reader.read_flatgeobuf(Path('fixture.fgb')))
        call = run.call_args
        self.assertIn('-features', call.args[0])
        self.assertNotIn('-so', call.args[0])
        self.assertEqual('YES', call.kwargs['env']['OGR_FLATGEOBUF_VERIFY_BUFFERS'])

    def test_gdal_errors_fail_even_if_exit_status_is_zero(self):
        result = subprocess.CompletedProcess([], 0, json.dumps(self.payload()), 'ERROR 1: invalid buffer')
        with patch.object(reader.subprocess, 'run', return_value=result), self.assertRaises(ValueError):
            reader.read_flatgeobuf(Path('fixture.fgb'))


@unittest.skipUnless(shutil.which('ogrinfo') or os.getenv('HONUA_CNG_REQUIRE_GDAL_VERSION'),
                     'real GDAL CLI not installed')
class FlatGeobufGdalIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        required = os.getenv('HONUA_CNG_REQUIRE_GDAL_VERSION')
        if required:
            if not shutil.which('ogrinfo'):
                raise AssertionError('Required GDAL CLI is missing')
            version = subprocess.run(['ogrinfo', '--version'], capture_output=True,
                                     text=True, check=True).stdout
            match = re.match(r'GDAL (\d+\.\d+\.\d+)(?:,|\s|$)', version)
            if not match or match.group(1) != required:
                raise AssertionError(f'Required GDAL {required}; observed {version.strip()}')

    def test_truncated_final_feature_fails_after_successful_summary_read(self):
        fixture = Path(__file__).resolve().parents[3] / 'tests/fixtures/external-format-corpus/v1/survey-sites.fgb'
        self.assertEqual(3, reader.require_decoded_features(reader.read_flatgeobuf(fixture)))
        with tempfile.TemporaryDirectory() as directory:
            damaged = Path(directory) / 'truncated.fgb'
            damaged.write_bytes(fixture.read_bytes()[:-8])
            header = subprocess.run(['ogrinfo', '-json', '-al', '-so', str(damaged)],
                                    capture_output=True, text=True, check=True)
            self.assertEqual(3, json.loads(header.stdout)['layers'][0]['featureCount'])
            with self.assertRaises((ValueError, subprocess.CalledProcessError)):
                reader.read_flatgeobuf(damaged)
