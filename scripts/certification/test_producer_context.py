import copy
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from producer_context import load_envelope, run_identity

SHA = 'a' * 40
DIGEST = 'sha256:' + 'b' * 64
CUT = '2026-10-13T00:00:00Z'


class ProducerContextTests(unittest.TestCase):
    def test_bounded_envelope_preserves_source_and_candidate_separation(self):
        value = {'schema': 'honua.protocol-producer-context/v1', 'producer': 'fixture',
                 'producer_source_sha': SHA, 'requirements_revision': 'governed.1',
                 'candidate': {'source_sha': 'c' * 40, 'image_digest': DIGEST, 'cut_at': CUT},
                 'requirements': [{'source_owned': 'not inferred here'}]}
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'context.json'
            path.write_text(json.dumps(value))
            self.assertEqual(load_envelope(path, 'fixture', 'c' * 40, SHA, DIGEST, CUT), value)
            for key in ['producer', 'producer_source_sha', 'candidate', 'schema']:
                invalid = copy.deepcopy(value)
                invalid[key] = 'wrong'
                path.write_text(json.dumps(invalid))
                with self.subTest(key=key), self.assertRaises(ValueError):
                    load_envelope(path, 'fixture', 'c' * 40, SHA, DIGEST, CUT)
            path.write_text('{"schema":1,"schema":2}')
            with self.assertRaisesRegex(ValueError, 'duplicate'): load_envelope(path, 'fixture', SHA, SHA, DIGEST, CUT)
            path.write_text(' ' * 60001)
            with self.assertRaisesRegex(ValueError, '60000'): load_envelope(path, 'fixture', SHA, SHA, DIGEST, CUT)

    def test_workflow_and_ref_are_both_bound(self):
        workflow = 'protocol-harness-certification.yml'
        ref = 'refs/tags/nightly-candidate/' + SHA
        env = {'GITHUB_REPOSITORY': 'honua-io/honua-server', 'GITHUB_SHA': SHA,
               'GITHUB_REF': ref, 'GITHUB_EVENT_NAME': 'workflow_dispatch',
               'GITHUB_WORKFLOW_REF': f'honua-io/honua-server/.github/workflows/{workflow}@{ref}',
               'GITHUB_RUN_ID': '12', 'GITHUB_RUN_ATTEMPT': '2',
               'NIGHTLY_DISPATCH_ID': 'nightly-certification-7-1'}
        with patch.dict(os.environ, env, clear=True):
            self.assertEqual(run_identity(SHA, workflow)['workflow'], workflow)
            with self.assertRaises(ValueError): run_identity(SHA, 'unreviewed.yml')
        with patch.dict(os.environ, {**env, 'GITHUB_REF': 'refs/heads/unreviewed'}, clear=True), self.assertRaises(ValueError):
            run_identity(SHA, workflow)


if __name__ == '__main__': unittest.main()
