"""Failure evidence must survive cleanup without publishing generated secrets."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

from compose_readiness_diagnostics import capture, readiness_diagnostics

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tests/integration'))
spec = importlib.util.spec_from_file_location(
    'manual_rollback', ROOT / 'tests/integration/test_compose_manual_rc1_rollback.py')
manual = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manual)


class ReadinessDiagnosticTests(unittest.TestCase):
    def evidence(self, states, logs, secret='short-private-value'):
        with patch('compose_readiness_diagnostics.read_secret', return_value=secret), \
                patch('compose_readiness_diagnostics.capture', side_effect=[states, logs]):
            return readiness_diagnostics(['docker', 'compose'], {}, Path('/unused'))

    def test_allowlists_state_and_redacts_generated_secrets_and_error_credentials(self):
        row = {'Service': 'honua', 'State': 'exited', 'Health': '', 'ExitCode': 1,
               'Command': 'password=must-not-publish', 'Labels': 'private'}
        for states in (json.dumps([row]), json.dumps(row) + '\n'):
            with self.subTest(states=states):
                detail = self.evidence(states, 'ERROR failed short-private-value Password=other; ' + 'x' * 40)
                self.assertIn('exited', detail)
                self.assertIn('ExitCode', detail)
                for private in ('must-not-publish', 'Labels', 'short-private-value', 'other', 'x' * 40):
                    self.assertNotIn(private, detail)
                self.assertIn('[redacted]', detail)

    def test_bounds_errors_and_omits_non_error_lines(self):
        logs = 'ordinary private output\n' + '\n'.join(f'ERROR {i} ' + '!' * 400 for i in range(12))
        detail = self.evidence('invalid JSON', logs)
        self.assertIn('services=unavailable', detail)
        self.assertNotIn('ordinary', detail)
        self.assertNotIn('ERROR 3 ', detail)
        self.assertIn('ERROR 11 ', detail)
        self.assertLess(len(detail), 2600)

    def test_unreadable_secret_inventory_suppresses_all_capture(self):
        with patch('compose_readiness_diagnostics.read_secret', side_effect=OSError), \
                patch('compose_readiness_diagnostics.capture') as command:
            self.assertIn('unavailable', readiness_diagnostics([], {}, Path('/unused')))
        command.assert_not_called()

    def test_command_failure_or_timeout_returns_no_stderr(self):
        for result in (OSError('private'), UnicodeError('private'), subprocess.TimeoutExpired('docker', 15)):
            with patch('compose_readiness_diagnostics.subprocess.run', side_effect=result):
                self.assertEqual('', capture([], {}, []))
        with patch('compose_readiness_diagnostics.subprocess.run', return_value=
                   subprocess.CompletedProcess([], 1, 'private stdout', 'private stderr')) as command:
            self.assertEqual('', capture([], {}, []))
        self.assertEqual(15, command.call_args.kwargs['timeout'])

    def test_preserves_readiness_failure_and_identifies_stage(self):
        original = AssertionError('manual deployment did not become ready')
        with patch.object(manual, 'wait_ready', side_effect=original), \
                patch.object(manual, 'readiness_diagnostics', return_value='services=exited'):
            with self.assertRaisesRegex(AssertionError, 'initial rc.1.*services=exited') as caught:
                manual.wait_ready_with_diagnostics(1234, 'initial rc.1', ([], {}, Path('/unused')))
        self.assertIs(original, caught.exception.__cause__)

    def test_failed_journey_collects_evidence_before_volume_cleanup(self):
        events = []
        env = {'COMPOSE_PROJECT_NAME': 'fixture'}
        def command(arguments, **kwargs):
            events.append(arguments)
            return 'fixture-image'
        def evidence(*args):
            events.append('evidence')
            return 'services=exited'
        with patch.dict(manual.os.environ, {'HONUA_LIFECYCLE_IMAGE': 'candidate'}), \
                patch.object(manual, 'run', side_effect=command), \
                patch.object(manual, 'manual_install', return_value=(Path('/unused'), 1234, env)), \
                patch.object(manual, 'legacy_configuration', return_value=(['legacy'], env)), \
                patch.object(manual, 'wait_ready', side_effect=AssertionError('not ready')), \
                patch.object(manual, 'readiness_diagnostics', side_effect=evidence):
            with self.assertRaisesRegex(AssertionError, 'initial published rc.1 startup'):
                manual.test_manual_catalog_rc1_rollback_and_return(Path('/unused'))
        self.assertEqual('evidence', events[-2])
        self.assertEqual(['down', '--volumes', '--remove-orphans'], events[-1][-3:])

    def test_success_does_not_collect_diagnostics(self):
        with patch.object(manual, 'wait_ready'), \
                patch.object(manual, 'readiness_diagnostics') as diagnostics:
            manual.wait_ready_with_diagnostics(1234, 'candidate', ([], {}, Path('/unused')))
        diagnostics.assert_not_called()


if __name__ == '__main__':
    unittest.main()
