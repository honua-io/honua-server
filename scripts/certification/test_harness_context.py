import copy
import base64
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path
from unittest.mock import patch

from test_generate_protocol_harness_fragment import (
    CONTRACT, REPOSITORY_ROOT, SCRIPT, contract, governed_fixture, module,
)


def execution_args():
    return Namespace(
        source_sha='a' * 40, producer_source_sha='a' * 40,
        image_digest='sha256:' + 'c' * 64, candidate_cut_at='2026-08-21T00:00:00Z',
        started_at='2026-08-21T01:00:00Z', completed_at='2026-08-21T01:01:00Z',
        generated_at='2026-08-21T01:02:00Z',
    )


class HarnessContextTests(unittest.TestCase):
    def test_v2_digest_binds_governed_context_run_and_source_host_identity(self):
        value = contract()
        outcomes = {'ExampleTests.Get_ReturnsDocument': 'pass'}
        with governed_fixture(value, execution_args()) as args:
            result = module.build_fragment(value, outcomes, args)
            receipt = result['observations'][0]['evidence_receipt']
            self.assertEqual(receipt['schema'], 'honua.certification-evidence-receipt/v2')
            identity = receipt['identity']
            self.assertEqual(identity['maturity'], 'supported')
            self.assertEqual(identity['required_tier'], 'nightly')
            self.assertEqual(identity['requirements_revision'], 'fixture-requirements.1')
            self.assertEqual(identity['candidate_cut_at'], args.candidate_cut_at)
            self.assertEqual(identity['producer_run']['dispatch_id'], 'nightly-certification-456-1')
            self.assertIsNone(identity['image_digest'])
            self.assertEqual(result['candidate']['image_digest'], args.image_digest)
            digest = 'sha256:' + hashlib.sha256(module.canonical_bytes(receipt)).hexdigest()
            self.assertEqual(result['observations'][0]['evidence_digest'], digest)
            context = json.loads(args.requirements_context.read_text())
            context['requirements_revision'] = 'fixture-requirements.2'
            args.requirements_context.write_text(json.dumps(context))
            changed = module.build_fragment(value, outcomes, args)
            self.assertNotEqual(changed['observations'][0]['evidence_digest'], digest)

    def test_execution_metadata_binds_source_host_semantics_without_inventing_url(self):
        value = contract()
        with governed_fixture(value, execution_args()) as args:
            observation = module.build_fragment(value, {'ExampleTests.Get_ReturnsDocument': 'pass'}, args)['observations'][0]
            payload = json.loads(base64.b64decode(observation['evidence_receipt']['payload_base64']))
            execution = payload['execution']
            self.assertEqual(execution, {key: observation[key] for key in execution})
            self.assertEqual(execution['client_id'], value['canonical_client'])
            self.assertEqual(execution['performed_by'], value['canonical_client'])
            self.assertEqual(execution['runner_lane'], value['client_lane'])
            self.assertEqual(execution['protocol_version'], value['revision'])
            self.assertEqual(execution['protocol_profile'], 'source-test-host-http-adapter')
            self.assertEqual(execution['exercised_capabilities'], value['assignments'][0]['scenario_facets'])
            self.assertIsNone(execution['request_url'])
            self.assertEqual(payload['test_outcomes'], {'ExampleTests.Get_ReturnsDocument': 'pass'})
            before = observation['evidence_digest']
            payload['execution']['request_url'] = 'https://unobserved.example.test/'
            changed = copy.deepcopy(observation['evidence_receipt'])
            changed['payload_base64'] = base64.b64encode(module.canonical_bytes(payload)).decode('ascii')
            self.assertNotEqual('sha256:' + hashlib.sha256(module.canonical_bytes(changed)).hexdigest(), before)

    def test_actual_matrix_emits_only_implemented_rows_but_filters_all_declared_tests(self):
        value = module.load_contract(CONTRACT)
        outcomes = {test: 'pass' for row in value['assignments'] for test in row['test_ids']}
        matrix_path = REPOSITORY_ROOT / 'docs/gis/data/capability-matrix.v1.json'
        matrix = json.loads(matrix_path.read_text())
        implemented = {row['key'] for row in matrix['capabilities'] if row.get('maturity', {}).get('implemented')}
        with governed_fixture(value, execution_args()) as args:
            args.matrix = matrix_path
            context = json.loads(args.requirements_context.read_text())
            context['requirements'] = [row for row in context['requirements'] if row['capability_key'] in implemented]
            args.requirements_context.write_text(json.dumps(context))
            result = module.build_fragment(value, outcomes, args)
            self.assertEqual(len(value['assignments']), 51)
            self.assertEqual(len(result['observations']), 42)
            self.assertEqual(result['operation_scope'], {'complete': True, 'expected': 42, 'observed': 42})
            selected = set(module.test_filter(value).split('|'))
            self.assertEqual(selected, {'FullyQualifiedName~' + test for test in outcomes})
            preview = [row for row in value['assignments'] if row['capability_key'] not in implemented]
            self.assertEqual(len(preview), 9)
            self.assertTrue(all('FullyQualifiedName~' + test in selected for row in preview for test in row['test_ids']))

    def test_context_must_match_every_source_owned_row_field(self):
        value = contract()
        value['assignments'][0]['test_ids'].append('ExampleTests.Get_RejectsInvalidInput')
        outcomes = {test: 'pass' for test in value['assignments'][0]['test_ids']}
        mutations = {
            'maturity': 'preview', 'required_tier': 'release', 'client_lane': 'unreviewed',
            'capability_key': 'serve.other', 'surface': 'other', 'operation': 'POST /example',
            'canonical_client': 'unreviewed', 'client_version': 'source@' + 'b' * 40,
            'deployment_target': 'local-docker', 'fixture_revision': 'other',
            'contract_revision': 'other', 'auth_policy_revision': 'other',
            'scenario_facets': ['positive'],
            'test_ids': list(reversed(value['assignments'][0]['test_ids'])),
            'extra': 'unreviewed',
        }
        for key, replacement in mutations.items():
            with self.subTest(field=key), governed_fixture(value, execution_args()) as args:
                context = json.loads(args.requirements_context.read_text())
                context['requirements'][0][key] = replacement
                args.requirements_context.write_text(json.dumps(context))
                with self.assertRaisesRegex(ValueError, 'altered harness operation'):
                    module.build_fragment(value, outcomes, args)

    def test_missing_duplicate_and_foreign_candidate_context_refuse(self):
        value = contract()
        outcomes = {'ExampleTests.Get_ReturnsDocument': 'pass'}
        for mutation in ('missing', 'duplicate', 'candidate', 'source', 'revision'):
            with self.subTest(mutation=mutation), governed_fixture(value, execution_args()) as args:
                context = json.loads(args.requirements_context.read_text())
                if mutation == 'missing': context['requirements'] = []
                elif mutation == 'duplicate': context['requirements'].append(copy.deepcopy(context['requirements'][0]))
                elif mutation == 'candidate': context['candidate']['image_digest'] = 'sha256:' + 'd' * 64
                elif mutation == 'source': context['producer_source_sha'] = 'b' * 40
                else: context['requirements_revision'] = ''
                args.requirements_context.write_text(json.dumps(context))
                with self.assertRaises(ValueError): module.build_fragment(value, outcomes, args)

    def test_source_and_workflow_identity_cannot_be_relabelled(self):
        value = contract()
        outcomes = {'ExampleTests.Get_ReturnsDocument': 'pass'}
        args = execution_args()
        args.source_sha = 'b' * 40
        with governed_fixture(value, args) as prepared, self.assertRaisesRegex(ValueError, 'SHAs must be identical'):
            module.build_fragment(value, outcomes, prepared)
        with governed_fixture(value, execution_args()) as args:
            with patch.dict(os.environ, {'GITHUB_WORKFLOW_REF': 'unreviewed'}), self.assertRaisesRegex(ValueError, 'workflow ref'):
                module.build_fragment(value, outcomes, args)

    def test_context_preflight_refuses_malformed_candidate_before_execution(self):
        for field, bad in [('source_sha', 'invalid'), ('producer_source_sha', 'invalid'),
                           ('image_digest', 'sha256:short'), ('candidate_cut_at', 'not-a-date')]:
            args = execution_args()
            setattr(args, field, bad)
            with self.subTest(field=field), self.assertRaises(ValueError):
                module.validate_candidate(args)

    def test_cli_refuses_contextless_evidence_without_writing_output(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'fragment.json'
            result = subprocess.run([sys.executable, str(SCRIPT), '--contract', str(CONTRACT),
                                     '--output', str(output)], capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('requirements_context', result.stderr)
            self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
