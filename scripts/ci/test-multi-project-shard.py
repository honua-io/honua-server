#!/usr/bin/env python3
"""A successful sibling must never mask an empty, failed or timed-out assembly."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import Mock, patch
import xml.etree.ElementTree as ET

SCRIPT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('multi', SCRIPT / 'run-multi-project-shard.py')
multi = importlib.util.module_from_spec(spec)
spec.loader.exec_module(multi)


class MultiProjectTests(unittest.TestCase):
    def run_group(self, outcomes):
        config = json.loads(Path('.github/ci-shards.json').read_text())
        projects = next(s['csprojs'] for s in config['shards'] if 'csprojs' in s)[:2]
        with tempfile.TemporaryDirectory() as directory:
            environment = {**os.environ, 'HONUA_SERVER_TEST_CSPROJS': json.dumps(projects),
                           'HONUA_SERVER_TEST_SHARD_NAME': 'group', 'HONUA_SERVER_TEST_LOG_NAME': 'group',
                           'HONUA_SERVER_TEST_FILTER': 'FullyQualifiedName~Honua',
                           'HONUA_SERVER_TEST_TIMEOUT_MINUTES': '20',
                           'HONUA_SERVER_TEST_RESULTS_DIR': directory}
            def execute(child, remaining):
                index = projects.index(child['HONUA_SERVER_TEST_CSPROJ'])
                outcome = outcomes[index]
                root = ET.Element('TestRun')
                results = ET.SubElement(root, 'Results')
                if outcome != 'empty':
                    ET.SubElement(results, 'UnitTestResult', outcome=outcome)
                counts = {'total': '0' if outcome == 'empty' else '1',
                          'executed': '0' if outcome == 'empty' else '1',
                          'passed': '1' if outcome == 'Passed' else '0',
                          'failed': '1' if outcome == 'Failed' else '0'}
                ET.SubElement(ET.SubElement(root, 'ResultSummary'), 'Counters', **counts)
                path = Path(child['HONUA_SERVER_TEST_RESULTS_DIR']) / child['HONUA_SERVER_TEST_LOG_NAME']
                ET.ElementTree(root).write(path.with_suffix('.trx'))
                path.with_suffix('.timing.json').write_text(json.dumps({
                    'status': 'passed' if outcome == 'Passed' else 'failed'}))
                return (0 if outcome in ('Passed', 'empty') else 1), 0
            with patch.dict(os.environ, environment, clear=True), patch.object(multi, 'execute', side_effect=execute):
                code = multi.main()
            self.assertEqual([Path(directory) / 'group.timing.json'],
                             list(Path(directory).rglob('*.timing.json')))
            self.assertEqual(2, len(list(Path(directory).rglob('*.member.json'))))
            record = json.loads((Path(directory) / 'group.timing.json').read_text())
            return code, record

    def test_all_projects_contribute_to_execution_evidence(self):
        code, record = self.run_group(['Passed', 'Passed'])
        self.assertEqual(0, code)
        self.assertEqual(2, record['execution']['counts']['executed'])
        self.assertEqual('passed', record['status'])

    def test_success_cannot_mask_failed_or_empty_sibling(self):
        for outcomes in (['Passed', 'Failed'], ['Failed', 'Passed'], ['Passed', 'empty']):
            with self.subTest(outcomes=outcomes):
                code, record = self.run_group(outcomes)
                self.assertNotEqual(0, code)
                self.assertEqual('failed', record['status'])

    def test_timeout_stops_group_and_removes_stale_sibling_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            stale = directory / 'project-1.trx'
            stale.write_text('stale passing results')
            with patch.object(multi, 'execute', return_value=(124, 0)):
                attempts, code = multi.run_projects(['a.csproj', 'b.csproj'],
                    {'HONUA_SERVER_TEST_TIMEOUT_MINUTES': '1'}, directory)
            self.assertEqual(124, code)
            self.assertEqual(1, len(attempts))
            self.assertFalse(stale.exists())

    def test_member_receives_fractional_remaining_budget(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(multi.time, 'monotonic', side_effect=[0, 30]), \
                    patch.object(multi, 'execute', return_value=(124, 0)) as execute:
                multi.run_projects(['a.csproj'],
                    {'HONUA_SERVER_TEST_TIMEOUT_MINUTES': '2'}, Path(directory))
            child, remaining = execute.call_args.args
            self.assertEqual(90, remaining)
            self.assertEqual('1.5', child['HONUA_SERVER_TEST_TIMEOUT_MINUTES'])
            self.assertEqual('true', child['HONUA_SERVER_TEST_TIMEOUT_FOREGROUND'])

    def test_group_timeout_keeps_capacity_and_hang_diagnostics(self):
        environment = {'HONUA_SERVER_TEST_TIMEOUT_MINUTES': '20',
                       'HONUA_SERVER_TEST_SHARD_NAME': 'group', 'HONUA_SERVER_TEST_LOG_NAME': 'group',
                       'HONUA_SERVER_TEST_FILTER': 'FullyQualifiedName~Honua',
                       'HONUA_SERVER_TEST_CSPROJS': '["a.csproj"]'}
        for idle, capacity in ((2, 'capacity_exhausted'), (301, 'hang_suspected')):
            with self.subTest(idle=idle):
                record = multi.timing_record(environment, (time.monotonic() - 1200, multi.utc_now()),
                                             (False, 124, {}, idle))
                self.assertEqual(capacity, record['capacity_status'])
                self.assertEqual('timed_out', record['status'])
                self.assertTrue(record['timed_out'])

    def test_windows_deadline_terminates_the_entire_tree(self):
        process = Mock(pid=4321)
        with patch.object(multi.os, 'name', 'nt'), patch.object(multi.subprocess, 'run') as run:
            multi.terminate_tree(process)
        run.assert_called_once_with(['taskkill', '/PID', '4321', '/T', '/F'],
                                    check=False, capture_output=True)
        process.wait.assert_called_once_with()

    def test_shared_deadline_stops_the_test_process(self):
        self.assert_deadline_cleanup(detach=False)

    def test_shared_deadline_stops_detached_test_process(self):
        self.assert_deadline_cleanup(detach=True)

    def assert_deadline_cleanup(self, detach):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            pid_file = root / 'test.pid'
            fake = root / 'dotnet'
            fake.write_text('#!/usr/bin/env python3\nimport os, signal, time\n'
                            'from pathlib import Path\n'
                            + ('os.setsid()\n' if detach else '') +
                            'signal.signal(signal.SIGTERM, signal.SIG_IGN)\n'
                            f'Path({str(pid_file)!r}).write_text(str(os.getpid()))\n'
                            'time.sleep(30)\n')
            fake.chmod(0o755)
            environment = {**os.environ, 'PATH': f'{root}:{os.environ["PATH"]}',
                           'HONUA_SERVER_TEST_CSPROJS': '',
                           'HONUA_SERVER_TEST_SHARD_NAME': 'deadline',
                           'HONUA_SERVER_TEST_LOG_NAME': 'deadline',
                           'HONUA_SERVER_TEST_FILTER': 'FullyQualifiedName~Honua',
                           'HONUA_SERVER_TEST_RESULTS_DIR': directory,
                           'HONUA_SERVER_TEST_TIMEOUT_MINUTES': '1',
                           'HONUA_SERVER_TEST_TIMEOUT_FOREGROUND': 'true'}
            self.assertEqual(124, multi.execute(environment, 2)[0])
            self.assertTrue(pid_file.exists(), 'Test process must have started')
            status = Path(f'/proc/{pid_file.read_text()}/stat')
            time.sleep(0.1)
            self.assertTrue(not status.exists() or status.read_text().split()[2] == 'Z',
                            'Test process survived the group deadline')


if __name__ == '__main__':
    unittest.main()
