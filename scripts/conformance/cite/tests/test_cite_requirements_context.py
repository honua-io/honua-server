import copy
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1]))
from build_protocol_certification_fragment import FIXTURE_BY_SUITE, SUITE_BY_SURFACE, run_identity
from requirements_context import load_context

SHA = 'a' * 40
DIGEST = 'sha256:' + 'b' * 64
CUT = '2026-10-13T00:00:00Z'
ROOT = Path(__file__).resolve().parents[4]


def valid_context():
    import re
    rows = []
    for capability in json.loads((ROOT / 'docs/gis/data/capability-matrix.v1.json').read_text())['capabilities']:
        for cite in capability.get('cite', []):
            suite = cite['suite']
            surface = re.sub(r'[^a-z0-9]+', '-', suite.lower()).strip('-')
            if surface not in SUITE_BY_SURFACE:
                continue
            rows.append({'capability_key': capability['key'], 'surface': surface, 'operation': capability['key'],
                         'canonical_client': 'OGC CITE', 'client_version': suite, 'client_lane': 'cite-' + surface,
                         'deployment_target': 'local-docker', 'fixture_revision': FIXTURE_BY_SUITE[SUITE_BY_SURFACE[surface]] + '@{source_sha}',
                         'contract_revision': 'server-capability-matrix@' + SHA, 'auth_policy_revision': 'anonymous-public-v1',
                         'scenario_facets': ['positive', 'negative', 'crs-axis', 'media-schema'],
                         'maturity': 'supported', 'required_tier': 'nightly'})
    return {'schema': 'honua.protocol-producer-context/v1', 'producer': 'honua-server-cite',
            'producer_source_sha': SHA, 'candidate': {'source_sha': SHA, 'image_digest': DIGEST, 'cut_at': CUT},
            'requirements_revision': 'test-staged.1', 'requirements': rows}


class ContextTests(unittest.TestCase):
    def load(self, value):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'context.json'
            path.write_text(json.dumps(value))
            return load_context(path, SHA, SHA, DIGEST, CUT, SUITE_BY_SURFACE, FIXTURE_BY_SUITE)

    def test_actual_source_roster_admitted_without_inferred_context(self):
        value = valid_context()
        self.assertEqual(self.load(value), {'revision': value['requirements_revision'], 'requirements': value['requirements']})
        self.assertTrue(any(row['surface'] == 'wps-2-0' for row in value['requirements']))

    def test_context_drift_refused(self):
        mutations = ['source', 'candidate', 'missing-row', 'duplicate', 'facet', 'fixture', 'tier', 'maturity', 'extra', 'test-ids']
        for mutation in mutations:
            value = valid_context()
            row = value['requirements'][0]
            if mutation == 'source': value['producer_source_sha'] = 'c' * 40
            elif mutation == 'candidate': value['candidate']['image_digest'] = 'sha256:' + 'd' * 64
            elif mutation == 'missing-row': value['requirements'].pop()
            elif mutation == 'duplicate': value['requirements'].append(copy.deepcopy(row))
            elif mutation == 'facet': row['scenario_facets'].pop()
            elif mutation == 'fixture': row['fixture_revision'] = 'unexecuted@{source_sha}'
            elif mutation == 'tier': row['required_tier'] = 'pr'
            elif mutation == 'maturity': row['maturity'] = 'preview'
            elif mutation == 'extra': value['unreviewed'] = True
            else: row['test_ids'] = ['invented']
            with self.subTest(mutation=mutation), self.assertRaises(ValueError): self.load(value)

    def test_duplicate_local_suite_refuses_and_cite_roster_does_not_infer_status_filter(self):
        value = valid_context()
        matrix = json.loads((ROOT / 'docs/gis/data/capability-matrix.v1.json').read_text())
        owner = next(cap for cap in matrix['capabilities'] if cap.get('cite'))
        # The canonical generator emits every declared CITE suite, independently
        # of the status filter used for SDK/harness assignments.
        owner['status'] = 'preview'
        with patch('requirements_context.Path.read_text', return_value=json.dumps(matrix)):
            self.assertEqual(len(self.load(value)['requirements']), len(value['requirements']))
        owner['cite'].append(copy.deepcopy(owner['cite'][0]))
        with patch('requirements_context.Path.read_text', return_value=json.dumps(matrix)):
            with self.assertRaisesRegex(ValueError, 'surface is duplicated'):
                self.load(value)

    def test_environment_identity_requires_actual_workflow_ref(self):
        env = {'GITHUB_REPOSITORY': 'honua-io/honua-server', 'GITHUB_SHA': SHA,
               'GITHUB_REF': 'refs/tags/nightly-candidate/' + SHA,
               'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_RUN_ID': '12', 'GITHUB_RUN_ATTEMPT': '2',
               'NIGHTLY_DISPATCH_ID': 'nightly-certification-7-1'}
        env['GITHUB_WORKFLOW_REF'] = 'honua-io/honua-server/.github/workflows/cite-certification.yml@' + env['GITHUB_REF']
        with patch.dict(os.environ, env, clear=True): self.assertEqual(run_identity(SHA)['run_attempt'], 2)
        for key, value in [('GITHUB_REF', 'refs/heads/unreviewed'), ('GITHUB_SHA', 'b' * 40),
                           ('GITHUB_RUN_ATTEMPT', '0'), ('NIGHTLY_DISPATCH_ID', 'manual'),
                           ('GITHUB_WORKFLOW_REF', 'wrong'), ('GITHUB_EVENT_NAME', 'pull_request')]:
            with self.subTest(key=key), patch.dict(os.environ, {**env, key: value}, clear=True), self.assertRaises(ValueError):
                run_identity(SHA)


if __name__ == '__main__': unittest.main()
