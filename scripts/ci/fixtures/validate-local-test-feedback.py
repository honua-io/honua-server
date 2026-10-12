#!/usr/bin/env python3
"""Service-free contracts for local affected-test feedback orchestration."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / 'local-test-feedback.py'
spec = importlib.util.spec_from_file_location('feedback', SCRIPT)
feedback = importlib.util.module_from_spec(spec)
spec.loader.exec_module(feedback)


class FeedbackTests(unittest.TestCase):
    def setUp(self):
        self.unit = 'tests/dotnet/Owner.UnitTests/Owner.UnitTests.csproj'
        self.component = 'tests/dotnet/Owner.ComponentTests/Owner.ComponentTests.csproj'
        self.mixed = feedback.MONOLITH
        self.catalog = {'projects': {
            self.unit: {'role': 'unit', 'isolated': True, 'resources': []},
            self.component: {'role': 'component', 'isolated': False, 'resources': ['postgres']},
            self.mixed: {'role': 'mixed', 'isolated': False, 'resources': ['postgres', 'redis']}}}
        self.registry = {'shards': [
            {'name': 'unit-id', 'shard_name': 'Unit', 'csproj': self.unit, 'filter': 'FullyQualifiedName~Unit'},
            {'shard_name': 'Component', 'csproj': self.component, 'filter': 'FullyQualifiedName~Component', 'timeout_minutes': 3},
            {'shard_name': 'Mixed', 'filter': 'FullyQualifiedName~Mixed'}]}

    def plan(self, selected=None, routed=None, all_shards=False):
        return feedback.make_plan(self.catalog, {'selectedProjects': selected or []},
                                  {'run_all': all_shards, 'shards': routed or [], 'reason': 'fixture'},
                                  self.registry, ['src/Owner/Changed.cs'])

    def test_full_unit_owns_overlap_but_retains_component_and_mixed_filters(self):
        result = self.plan([self.unit], ['unit-id', 'Component', 'Mixed'])
        self.assertEqual(result['projects'], sorted([self.unit, self.component, self.mixed]))
        self.assertEqual([s['name'] for s in result['shards']], ['Component', 'Mixed'])
        self.assertNotIn('Tier!=Fast', result['shards'][0]['effectiveFilter'])
        self.assertIn('Tier!=Fast', result['shards'][1]['effectiveFilter'])
        self.assertEqual(result['resources'], ['postgres', 'redis'])
        self.assertFalse(result['validatesPullRequest'])
        self.assertTrue(result['fullGatesRequired'])

    def test_router_broadening_keeps_all_shards_without_private_mapping(self):
        result = self.plan(all_shards=True)
        self.assertEqual(len(result['shards']), 3)
        self.assertFalse(result['emptySelection'])

    def test_unknown_shard_fails_instead_of_silent_omission(self):
        with self.assertRaisesRegex(ValueError, 'Unknown routed shards'):
            self.plan(routed=['unknown'])

    def test_malformed_router_receipts_fail_closed(self):
        for routing in [{}, {'run_all': 'false', 'shards': []}, {'run_all': False},
                        {'run_all': False, 'shards': 'Unit'}, {'run_all': True, 'shards': [None]}]:
            with self.subTest(routing=routing), self.assertRaises(ValueError):
                feedback.make_plan(self.catalog, {'selectedProjects': []}, routing, self.registry, [])

    def test_architecture_rejected(self):
        self.catalog['projects'][self.component]['role'] = 'architecture'
        with self.assertRaisesRegex(ValueError, 'Architecture'):
            self.plan(routed=['Component'])

    def test_empty_selection_is_explicit_and_executes_nothing(self):
        result = self.plan()
        self.assertTrue(result['emptySelection'])
        feedback.execute(Path('.'), result, run=lambda *a, **k: self.fail('unexpected execution'))

    def test_single_admitted_build_then_no_build_tests_and_canonical_shard(self):
        result = self.plan([self.unit], ['Unit', 'Component'])
        calls = []
        def run(command, **kwargs):
            calls.append((command, kwargs))
            if command[:2] == ['dotnet', 'test']:
                directory = Path(command[command.index('--results-directory') + 1])
            elif command[0] == 'bash':
                directory = Path(kwargs['env']['HONUA_SERVER_TEST_RESULTS_DIR'])
            else:
                return
            (directory / 'feedback.trx').write_text('<TestRun><ResultSummary><Counters total="2" executed="2"/></ResultSummary></TestRun>')
        with tempfile.TemporaryDirectory() as root:
            feedback.execute(Path(root), result, run=run)
        self.assertEqual([c[0][0:2] for c in calls], [['dotnet', 'build'], ['dotnet', 'test'], ['bash', 'scripts/ci/run-server-test-shard.sh']])
        self.assertIn('--no-build', calls[1][0])
        self.assertIn('--no-restore', calls[1][0])
        self.assertEqual(calls[2][1]['env']['HONUA_SERVER_TEST_FILTER'], 'FullyQualifiedName~Component')
        self.assertTrue(all(c[1]['check'] for c in calls))

    def test_zero_or_missing_trx_fails_even_if_executor_exits_successfully(self):
        for contents in [None, '<TestRun><Counters total="0"/></TestRun>',
                         '<TestRun><Counters total="1" executed="0" notExecuted="1"/></TestRun>']:
            with self.subTest(contents=contents), tempfile.TemporaryDirectory() as root:
                if contents:
                    (Path(root) / 'empty.trx').write_text(contents)
                with self.assertRaisesRegex(ValueError, 'No tests recorded'):
                    feedback.require_tests(root)

    def test_empty_diff_never_invokes_router_default_smoke(self):
        with patch.object(feedback.subprocess, 'check_output') as command:
            self.assertEqual(feedback.route(Path('.'), []),
                             {'run_all': False, 'shards': [], 'reason': 'no_changes'})
            command.assert_not_called()

    def test_failure_retains_plan_logs_status_without_deleting_caller_files(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            results = root / 'evidence'
            results.mkdir()
            marker = results / 'keep.txt'
            marker.write_text('caller-owned')
            def fail(command, **kwargs):
                kwargs['stdout'].write('diagnostic output')
                raise subprocess.CalledProcessError(1, command)
            with self.assertRaises(subprocess.CalledProcessError):
                feedback.execute(root, self.plan([self.unit]), run=fail, results_directory=results)
            runs = [p for p in results.iterdir() if p.is_dir()]
            self.assertEqual(len(runs), 1)
            self.assertEqual(marker.read_text(), 'caller-owned')
            self.assertTrue((runs[0] / 'plan.json').is_file())
            self.assertEqual((runs[0] / 'command-0.log').read_text(), 'diagnostic output')
            status = json.loads((runs[0] / 'result.json').read_text())
            self.assertEqual(status['status'], 'failed')
            self.assertFalse(status['validatesPullRequest'])
            self.assertFalse(list(root.glob('.honua-local-feedback-*')))

    def test_cancellation_records_terminal_status_and_retains_evidence(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            def cancel(command, **kwargs):
                raise KeyboardInterrupt()
            with self.assertRaises(KeyboardInterrupt):
                feedback.execute(root, self.plan([self.unit]), run=cancel)
            results = list((root / 'tests/TestResults/local-feedback').iterdir())
            status = json.loads((results[0] / 'result.json').read_text())
            self.assertEqual(status['status'], 'cancelled')
            self.assertFalse(status['validatesPullRequest'])
            self.assertTrue(Path(status['commands'][0]['log']).is_absolute())

    def test_namespaced_trx_is_supported(self):
        with tempfile.TemporaryDirectory() as root:
            (Path(root) / 'good.trx').write_text('<TestRun xmlns="urn:test"><Counters total="1" executed="1"/></TestRun>')
            feedback.require_tests(root)

    def test_build_failure_stops_before_tests(self):
        calls = []
        def fail(command, **kwargs):
            calls.append(command)
            raise subprocess.CalledProcessError(1, command)
        with tempfile.TemporaryDirectory() as root:
            with self.assertRaises(subprocess.CalledProcessError):
                feedback.execute(Path(root), self.plan([self.unit]), run=fail)
        self.assertEqual(len(calls), 1)

    def test_base_worktree_and_untracked_changes_share_one_changed_list(self):
        with patch.object(feedback.subprocess, 'check_output', side_effect=[b'src/a.cs\0tests/b.cs\0', b'new.cs\0src/a.cs\0']) as command:
            self.assertEqual(feedback.changed_files(Path('/fixture'), 'base'), ['new.cs', 'src/a.cs', 'tests/b.cs'])
        self.assertEqual(command.call_args_list[0].args[0], ['git', 'diff', '--name-only', '-z', 'base'])
        self.assertEqual(command.call_args_list[1].args[0], ['git', 'ls-files', '--others', '--exclude-standard', '-z'])


if __name__ == '__main__':
    unittest.main()
