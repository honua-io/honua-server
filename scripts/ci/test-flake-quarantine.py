#!/usr/bin/env python3
"""Quarantine expiry and retry completeness must fail closed."""
from datetime import datetime, timedelta, timezone
import importlib.util
import json
import os
import signal
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location('quarantine', Path(__file__).with_name('flake_quarantine.py'))
q = importlib.util.module_from_spec(spec)
spec.loader.exec_module(q)
PROJECT = 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'
NOW = datetime(2026, 11, 10, tzinfo=timezone.utc)


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


runner = module('retry', 'retry-failed-shard-tests.py')
publisher = module('publisher', 'quarantine-issues.py')


def entry(index=0):
    return dict(project=PROJECT, test=f'Honua.Tests.Case.Test{index}', issue=index + 1,
                quarantined_at=NOW.isoformat(), expires_at=(NOW + timedelta(days=14)).isoformat())


def trx(path, cases):
    root = ET.Element('TestRun')
    results = ET.SubElement(root, 'Results')
    definitions = ET.SubElement(root, 'TestDefinitions')
    for index, (name, outcome) in enumerate(cases):
        identity = str(index)
        ET.SubElement(results, 'UnitTestResult', testId=identity, testName=name, outcome=outcome)
        definition = ET.SubElement(definitions, 'UnitTest', id=identity)
        ET.SubElement(definition, 'TestMethod', className='Honua.Tests.Case', name=name.split('(')[0])
    ET.SubElement(ET.SubElement(root, 'ResultSummary'), 'Counters',
                  total=str(len(cases)), executed=str(sum(o != 'NotExecuted' for _, o in cases)),
                  passed=str(sum(o == 'Passed' for _, o in cases)), failed=str(sum(o == 'Failed' for _, o in cases)))
    ET.ElementTree(root).write(path)


class PolicyTests(unittest.TestCase):
    def test_expired_entries_regate_and_capacity_is_enforced(self):
        self.assertEqual(1, len(q.active_entries([entry()], NOW)))
        self.assertEqual([], q.active_entries([entry()], NOW + timedelta(days=14)))
        with self.assertRaises(ValueError):
            q.active_entries([entry(i) for i in range(26)], NOW)

    def test_invalid_duplicate_or_extended_entries_are_rejected(self):
        bad_values = [dict(test='A|FullyQualifiedName~B'), dict(project='../evil.csproj'),
                      dict(expires_at=(NOW + timedelta(days=15)).isoformat()), dict(issue=0),
                      dict(quarantined_at=(NOW + timedelta(hours=1)).isoformat())]
        for delta in bad_values:
            with self.subTest(delta=delta), self.assertRaises(ValueError):
                q.active_entries([{**entry(), **delta}], NOW)
        with self.assertRaises(ValueError):
            q.active_entries([entry(), entry()], NOW)

    def test_isolated_fast_gate_excludes_the_same_quarantined_method(self):
        catalog = module('catalog', 'test-projects.py')
        with tempfile.TemporaryDirectory() as directory:
            snapshot = Path(directory) / 'quarantine.json'
            current = datetime.now(timezone.utc)
            active = {**entry(), 'quarantined_at': current.isoformat(),
                      'expires_at': (current + timedelta(days=14)).isoformat()}
            snapshot.write_text(json.dumps({'schema': q.SCHEMA, 'entries': [active]}))
            with patch.dict(os.environ, {'HONUA_TEST_QUARANTINE_FILE': str(snapshot)}), \
                    patch.object(catalog.subprocess, 'run') as execute:
                catalog.execute_gate_fast(q.ROOT, [PROJECT])
            command = execute.call_args.args[0]
            self.assertEqual('(Tier=Fast)&FullyQualifiedName!=Honua.Tests.Case.Test0',
                             command[command.index('--filter') + 1])

    def test_exclusion_is_project_specific_and_exact(self):
        self.assertEqual('(Tier=Integration)&FullyQualifiedName!=Honua.Tests.Case.Test0',
                         q.exclude_filter('Tier=Integration', [entry()], PROJECT))
        self.assertEqual('Tier=Integration', q.exclude_filter('Tier=Integration', [entry()], 'other.csproj'))


class DuplicateCoverageTests(unittest.TestCase):
    def test_unfiltered_foundation_command_gets_exact_exclusion(self):
        command = ['dotnet', 'test', PROJECT]
        with patch.dict(os.environ, {'HONUA_TEST_QUARANTINE_FILE': 'snapshot.json'}, clear=True), \
                patch.object(runner, 'load_snapshot', return_value=[entry()]):
            filtered = runner.snapshot_filter(command, PROJECT)
        self.assertEqual([*command, '--filter', 'FullyQualifiedName!=Honua.Tests.Case.Test0'], filtered)


class RetryTests(unittest.TestCase):
    def compare(self, retry_cases):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        trx(root / 'first.xml', [('Stable', 'Passed'), ('Flaky(1)', 'Failed'), ('Flaky(2)', 'Failed')])
        trx(root / 'retry.xml', retry_cases)
        return q.retry_replacements(root / 'first.xml', root / 'retry.xml')

    def test_only_complete_actual_passing_retry_is_accepted(self):
        replacements = self.compare([('Flaky(1)', 'Passed'), ('Flaky(2)', 'Passed')])
        self.assertEqual(2, len(replacements))

    def test_missing_failed_skipped_unknown_and_unrelated_retry_cannot_pass(self):
        for cases in ([], [('Flaky(1)', 'Passed')], [('Flaky(1)', 'Passed'), ('Flaky(2)', 'Failed')],
                      [('Flaky(1)', 'Passed'), ('Flaky(2)', 'NotExecuted')],
                      [('Flaky(1)', 'Passed'), ('Flaky(2)', 'Error')], [('Other', 'Passed')]):
            with self.subTest(cases=cases), self.assertRaises(ValueError):
                self.compare(cases)

    def test_ambiguous_theory_rows_cannot_hide_an_omitted_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            first, retry = Path(directory) / 'first.xml', Path(directory) / 'retry.xml'
            for original, repeated in (
                    ([('Flaky(...)', 'Failed'), ('Flaky(...)', 'Passed')], [('Flaky(...)', 'Passed')]),
                    ([('Flaky(...)', 'Failed')], [('Flaky(...)', 'Passed'), ('Flaky(...)', 'Passed')])):
                trx(first, original)
                trx(retry, repeated)
                with self.subTest(original=original), self.assertRaises(ValueError):
                    q.retry_replacements(first, retry)

    def test_run_level_errors_cannot_be_reclassified_as_a_flake(self):
        for key in ('error', 'timeout', 'aborted', 'disconnected', 'notRunnable'):
            with self.subTest(key=key), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / 'run.xml'
                trx(path, [('Flaky', 'Failed')])
                tree = ET.parse(path)
                tree.find('.//Counters').set(key, '1')
                tree.write(path)
                with self.assertRaises(ValueError):
                    q.read_cases(path)

    def test_xunit_failure_and_warning_messages_allow_retry_but_host_errors_do_not(self):
        messages = [('Error', '[xUnit.net 00:00:01.52]     Flaky [FAIL]', True),
                    ('Warning', "Data collector 'Blame': All tests finished", True),
                    ('Error', 'Test host crashed', False),
                    ('Error', '[xUnit.net 00:00:01.52] host crashed', False)]
        for outcome, text, accepted in messages:
            with self.subTest(text=text), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / 'run.xml'
                trx(path, [('Flaky', 'Failed')])
                tree = ET.parse(path)
                info = ET.SubElement(ET.SubElement(tree.find('.//ResultSummary'), 'RunInfos'), 'RunInfo', outcome=outcome)
                ET.SubElement(info, 'Text').text = text
                tree.write(path)
                if accepted:
                    self.assertEqual('Failed', q.read_cases(path)[0][2])
                else:
                    with self.assertRaises(ValueError):
                        q.read_cases(path)

    def test_counter_tampering_cannot_supply_retry_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'bad.xml'
            trx(path, [('Flaky', 'Passed')])
            tree = ET.parse(path)
            tree.find('.//Counters').set('passed', '5')
            tree.write(path)
            with self.assertRaises(ValueError):
                q.read_cases(path)


class RunnerTests(unittest.TestCase):
    def test_duplicate_gate_wrapper_cleans_up_term_ignoring_child(self):
        self.assert_timeout_cleanup(foreground=False)

    def test_foreground_timeout_kills_term_ignoring_test_group(self):
        self.assert_timeout_cleanup(foreground=True)

    def test_timeout_cleans_up_descendant_outside_inherited_group(self):
        self.assert_timeout_cleanup(foreground=False, detached=True)

    def assert_timeout_cleanup(self, foreground, detached=False):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fake = root / 'dotnet'
            fake.write_text('#!/usr/bin/env python3\n'
                            'import os, pathlib, signal, time\n'
                            'signal.signal(signal.SIGTERM, signal.SIG_IGN)\n'
                            'is_child = os.fork() == 0\n'
                            f'if is_child and {detached}: os.setsid()\n'
                            'suffix = ".descendant.pid" if is_child else ".pid"\n'
                            'pathlib.Path(__file__).with_suffix(suffix).write_text(str(os.getpid()))\n'
                            'time.sleep(30)\n')
            fake.chmod(0o755)
            def cleanup():
                for suffix in ('.pid', '.descendant.pid'):
                    path = fake.with_suffix(suffix)
                    if path.exists():
                        try:
                            os.kill(int(path.read_text()), signal.SIGKILL)
                        except ProcessLookupError:
                            pass
            command = ['timeout', *(['--foreground'] if foreground else []), '--kill-after=1', '1', sys.executable,
                       str(Path(runner.__file__).with_name('run-quarantine-filtered-tests.py')),
                       'dotnet', 'test', PROJECT]
            with patch.dict(os.environ, {'PATH': f'{directory}:{os.environ["PATH"]}',
                                         'HONUA_TEST_QUARANTINE_OBSERVE': 'true',
                                         'HONUA_SERVER_TEST_TIMEOUT_FOREGROUND': str(foreground).lower()}):
                try:
                    result = subprocess.run(command, timeout=5, capture_output=True)
                except BaseException:
                    cleanup()
                    raise
            self.assertEqual(124, result.returncode)
            for suffix in ('.pid', '.descendant.pid'):
                process = Path('/proc') / fake.with_suffix(suffix).read_text() / 'stat'
                if process.exists():
                    self.assertEqual('Z', process.read_text().split(') ', 1)[1].split()[0])

    def test_retry_is_bounded_by_the_original_outer_timeout(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = root / 'first.trx'
            trx(first, [('Flaky', 'Failed')])
            fake = root / 'fake-test.py'
            fake.write_text('import pathlib, sys, time\n'
                            'root = pathlib.Path(__file__).parent\n'
                            'logger = sys.argv[sys.argv.index("--logger") + 1]\n'
                            'if "retry-attempt" in logger:\n'
                            '    time.sleep(30)\n'
                            'else:\n'
                            '    (root / "run.trx").write_bytes((root / "first.trx").read_bytes())\n'
                            '    sys.exit(1)\n')
            command = ['timeout', '--kill-after=1', '2', sys.executable, str(Path(runner.__file__)),
                       '--project', PROJECT, '--results', directory, '--log', 'run', '--',
                       sys.executable, str(fake), '--filter', 'Tier=Integration',
                       '--logger', 'trx;LogFileName=run.trx']
            result = subprocess.run(command, timeout=5, capture_output=True)
            self.assertEqual(124, result.returncode)
            self.assertTrue((root / 'run.first-attempt.xml').exists())
            self.assertFalse((root / 'run.flake-candidates.json').exists())

    def attempt(self, retry_cases, retry_code=0, enabled='true'):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        options = SimpleNamespace(project=PROJECT, output=root / 'run.trx', first=root / 'run.first-attempt.xml',
                                  retry=root / 'run.retry-attempt.xml', candidates=root / 'run.flake-candidates.json',
                                  command=['dotnet', 'test', PROJECT, '--filter', 'Tier=Integration',
                                           '--logger', 'trx;LogFileName=run.trx'])
        def execute(command):
            if command == options.command:
                trx(options.output, [('Stable', 'Passed'), ('Flaky(1)', 'Failed')])
                return 1
            if retry_cases is not None:
                trx(options.retry, retry_cases)
            return retry_code
        environment = {'HONUA_SERVER_TEST_RETRY_FAILED': enabled}
        with patch.dict(os.environ, environment, clear=True), patch.object(runner, 'call_test', side_effect=execute) as call:
            code = runner.run(options)
        return code, options, call

    def test_complete_retry_preserves_raw_evidence_and_final_counts(self):
        code, options, calls = self.attempt([('Flaky(1)', 'Passed')])
        self.assertEqual(0, code)
        self.assertEqual(2, calls.call_count)
        self.assertIn('(Tier=Integration)&(FullyQualifiedName=Honua.Tests.Case.Flaky)', calls.call_args.args[0])
        self.assertEqual(['Passed', 'Passed'], [case[2] for case in q.read_cases(options.output)])
        self.assertEqual(['Passed', 'Failed'], [case[2] for case in q.read_cases(options.first)])
        self.assertEqual(1, len(publisher.candidate_entries(options.output.parent)))

    def test_partial_retry_and_retry_process_failures_remain_red(self):
        for cases, exit_code in ((None, 0), ([], 0), ([('Other', 'Passed')], 0),
                                 ([('Flaky(1)', 'Failed')], 1), ([('Flaky(1)', 'Passed')], 124)):
            with self.subTest(cases=cases, exit_code=exit_code):
                code, options, calls = self.attempt(cases, exit_code)
                self.assertNotEqual(0, code)
                self.assertEqual(2, calls.call_count)
                self.assertFalse(options.candidates.exists())

    def test_observation_mode_keeps_the_first_failure(self):
        code, options, calls = self.attempt([('Flaky(1)', 'Passed')], enabled='false')
        self.assertEqual(1, code)
        self.assertEqual(1, calls.call_count)
        self.assertFalse(options.candidates.exists())

    def test_publisher_rejects_changed_raw_evidence(self):
        _, options, _ = self.attempt([('Flaky(1)', 'Passed')])
        options.retry.write_text('changed')
        with self.assertRaisesRegex(ValueError, 'digest mismatch'):
            publisher.candidate_entries(options.output.parent)


class PublisherTests(unittest.TestCase):
    def publish(self, active, existing=None):
        candidate = dict(project=PROJECT, test='Honua.Tests.Case.Flaky')
        key = publisher.title(candidate)
        with patch.object(publisher, 'candidate_entries', return_value={key: candidate}), \
                patch.object(publisher, 'ensure_labels'), \
                patch.object(publisher, 'issues', return_value=existing or []), \
                patch.object(publisher, 'current_entries', return_value=active), \
                patch.dict(os.environ, {'GITHUB_REPOSITORY': 'example/repo', 'QUARANTINE_EVIDENCE_URL': 'https://example.com/run'}), \
                patch.object(publisher, 'api') as api:
            publisher.publish(Path('.'))
        return api

    def test_cap_files_issue_without_excluding_a_twenty_sixth_test(self):
        api = self.publish([entry(i) for i in range(25)])
        self.assertEqual([publisher.LABEL], api.call_args.args[1]['labels'])

    def test_existing_issue_is_reused_and_active_expiry_is_not_extended(self):
        candidate = dict(project=PROJECT, test='Honua.Tests.Case.Flaky')
        issue = dict(title=publisher.title(candidate), number=123, user=dict(login='github-actions[bot]'))
        api = self.publish([], [issue])
        self.assertTrue(api.call_args.args[0].endswith('/issues/123'))
        self.assertEqual('open', api.call_args.args[1]['state'])
        api = self.publish([{**entry(), **candidate}], [issue])
        api.assert_not_called()


if __name__ == '__main__':
    unittest.main()
