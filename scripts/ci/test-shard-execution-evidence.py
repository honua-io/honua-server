#!/usr/bin/env python3
"""Offline guard controls; actual VSTest no-match proof is recorded separately."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('shard_execution', ROOT / 'scripts/ci/shard-execution-evidence.py')
evidence = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evidence)


def trx(path, outcomes, *, declared=None):
    root = ET.Element('TestRun', xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    rows = ET.SubElement(root, 'Results')
    for index, outcome in enumerate(outcomes):
        ET.SubElement(rows, 'UnitTestResult', testName=f'case{index}', outcome=outcome)
    counts = {'total': str(len(outcomes)), 'executed': str(sum(o != 'NotExecuted' for o in outcomes)),
              'passed': str(outcomes.count('Passed')), 'failed': str(outcomes.count('Failed'))}
    ET.SubElement(ET.SubElement(root, 'ResultSummary'), 'Counters', **(declared or counts))
    ET.ElementTree(root).write(path)


class EvidenceTests(unittest.TestCase):
    def test_measured_results_and_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'test.trx'
            for outcomes in (['Passed'], ['Passed', 'NotExecuted']):
                with self.subTest(outcomes=outcomes):
                    trx(path, outcomes)
                    result = evidence.observe(path, 'test', 'test.csproj')
                    self.assertEqual('passed', result['status'])
                    self.assertEqual(1, result['counts']['executed'])
                    self.assertEqual(len(outcomes), result['counts']['total'])
                    self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), result['trxSha256'])
                    self.assertEqual(40, len(result['sourceSha']))
                    self.assertEqual(64, len(result['runnerSha256']))

    def test_empty_skipped_failed_unknown_and_summary_only_cannot_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'test.trx'
            for outcomes in ([], ['NotExecuted'], ['Failed'], ['Passed', 'Failed'], ['Error'], ['Passed', 'Unknown']):
                with self.subTest(outcomes=outcomes):
                    trx(path, outcomes)
                    result = evidence.observe(path, 'test', 'test.csproj')
                    self.assertEqual('failed', result['status'])
            trx(path, [], declared={'total': '5', 'executed': '5', 'passed': '5', 'failed': '0'})
            self.assertIn('disagree', evidence.observe(path, 'test', 'test.csproj')['problem'])

    def test_missing_malformed_and_absent_counters_cannot_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'test.trx'
            self.assertEqual('failed', evidence.observe(path, 'test', 'test.csproj')['status'])
            path.write_text('<bad')
            self.assertEqual('failed', evidence.observe(path, 'test', 'test.csproj')['status'])
            path.write_text('<TestRun><Results><UnitTestResult outcome="Passed" /></Results></TestRun>')
            self.assertIn('no result counters', evidence.observe(path, 'test', 'test.csproj')['problem'])

    def run_supervisor(self, directory, *, mode='passed', child_exit=0, max_cpu='', no_jq=False, slow_evidence=False):
        root = Path(directory)
        stub = root / 'dotnet'
        stub.write_text('#!' + sys.executable + '''
import json, os, sys
from pathlib import Path
import xml.etree.ElementTree as ET
args = sys.argv[1:]
Path(os.environ['CAPTURE']).write_text(json.dumps(args))
mode = os.environ['MODE']
if mode != 'missing':
    results = Path(args[args.index('--results-directory') + 1])
    logger = next(v for v in args if v.startswith('trx;LogFileName='))
    root = ET.Element('TestRun')
    rows = ET.SubElement(root, 'Results')
    outcomes = {'passed':['Passed'], 'empty':[], 'skipped':['NotExecuted'], 'failed':['Failed']}[mode]
    for o in outcomes: ET.SubElement(rows, 'UnitTestResult', testName='synthetic', outcome=o)
    ET.SubElement(ET.SubElement(root, 'ResultSummary'), 'Counters', total=str(len(outcomes)), executed=str(sum(o!='NotExecuted' for o in outcomes)), passed=str(outcomes.count('Passed')), failed=str(outcomes.count('Failed')))
    ET.ElementTree(root).write(results / logger.split('=',1)[1])
sys.exit(int(os.environ['CHILD_EXIT']))
''')
        stub.chmod(0o755)
        env = {**os.environ, 'PATH': str(root) + os.pathsep + os.environ['PATH'],
               'CAPTURE': str(root / 'args.json'), 'MODE': mode, 'CHILD_EXIT': str(child_exit),
               'HONUA_SERVER_TEST_SHARD_NAME': 'synthetic guard control',
               'HONUA_SERVER_TEST_FILTER': 'FullyQualifiedName~Synthetic', 'HONUA_SERVER_TEST_LOG_NAME': 'control',
               'HONUA_SERVER_TEST_RESULTS_DIR': str(root), 'HONUA_SERVER_TEST_POLL_SECONDS': '0.01',
               'HONUA_SERVER_TEST_TIMEOUT_MINUTES': '', 'HONUA_SERVER_TEST_MAX_CPU_COUNT': max_cpu}
        if no_jq:
            bash_env = root / 'no-jq.sh'
            bash_env.write_text('command() { if [[ "$1" == -v && "${2:-}" == jq ]]; then return 1; fi; builtin command "$@"; }\n')
            env['BASH_ENV'] = str(bash_env)
        if slow_evidence:
            python = root / 'python3'
            python.write_text('#!' + sys.executable + "\nimport os, sys, time\n"
                              "if sys.argv[1].endswith('shard-execution-evidence.py'): time.sleep(3)\n"
                              "os.execv(sys.executable, [sys.executable, *sys.argv[1:]])\n")
            python.chmod(0o755)
            env.update(HONUA_SERVER_TEST_TIMEOUT_MINUTES='0.0167', HONUA_SERVER_TEST_KILL_AFTER_SECONDS='1',
                       HONUA_SERVER_TEST_KILL_ESCALATION_TOLERANCE_SECONDS='0')
        run = subprocess.run(['bash', str(ROOT / 'scripts/ci/run-server-test-shard.sh')], env=env, capture_output=True, text=True, timeout=15)
        return run, json.loads((root / 'control.timing.json').read_text()), json.loads((root / 'args.json').read_text())

    def test_supervisor_refuses_zero_and_stale_success(self):
        with tempfile.TemporaryDirectory() as directory:
            for mode in ('empty', 'skipped', 'missing', 'failed'):
                with self.subTest(mode=mode):
                    # No new output must never reuse an earlier attempt's successful TRX.
                    trx(Path(directory) / 'control.trx', ['Passed'])
                    run, timing, _ = self.run_supervisor(directory, mode=mode)
                    self.assertNotEqual(0, run.returncode)
                    self.assertEqual('failed', timing['status'])
                    self.assertEqual('failed', timing['execution']['status'])
                    if mode == 'missing': self.assertIsNone(timing['execution']['trxSha256'])

    def test_real_exit_failure_is_preserved_even_with_positive_trx(self):
        with tempfile.TemporaryDirectory() as directory:
            run, timing, _ = self.run_supervisor(directory, child_exit=7)
            self.assertEqual(7, run.returncode)
            self.assertEqual('failed', timing['status'])
            self.assertEqual('passed', timing['execution']['status'])

    def test_count_provenance_survives_without_jq(self):
        with tempfile.TemporaryDirectory() as directory:
            run, timing, _ = self.run_supervisor(directory, no_jq=True)
            self.assertEqual(0, run.returncode, run.stdout + run.stderr)
            self.assertEqual(1, timing['execution']['counts']['executed'])

    def test_evidence_io_does_not_reclassify_early_kill_as_timeout(self):
        with tempfile.TemporaryDirectory() as directory:
            run, timing, _ = self.run_supervisor(directory, child_exit=137, slow_evidence=True)
            self.assertEqual(137, run.returncode)
            self.assertEqual('killed', timing['status'])
            self.assertFalse(timing['timed_out'])
            self.assertLess(timing['duration_seconds'], 2)

    def test_no_match_setting_and_cpu_share_one_delimiter(self):
        for cpu in ('', '2'):
            with tempfile.TemporaryDirectory() as directory:
                run, timing, args = self.run_supervisor(directory, max_cpu=cpu)
                self.assertEqual(0, run.returncode, run.stdout + run.stderr)
                self.assertEqual('passed', timing['status'])
                self.assertEqual(1, timing['execution']['counts']['passed'])
                self.assertEqual(1, args.count('--'))
                self.assertIn('RunConfiguration.TreatNoTestsAsError=true', args[args.index('--')+1:])
                self.assertEqual(bool(cpu), 'RunConfiguration.MaxCpuCount=2' in args)


if __name__ == '__main__':
    unittest.main()
