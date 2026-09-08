#!/usr/bin/env python3
"""Exercise the runner contract without compiling .NET or starting containers."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
RUNNER = ROOT / 'scripts/ci/run-foundation-family.sh'
PLAN = (Path(__file__).with_name('foundation-test-plan.txt')).read_text().splitlines()


class FoundationExecution(unittest.TestCase):
    def test_plan_parity(self):
        families = subprocess.check_output([RUNNER, 'families'], text=True).split()
        self.assertEqual(len(families), 3)
        actual = []
        for family in families:
            actual.extend(subprocess.check_output([RUNNER, 'list', family], text=True).splitlines())
        self.assertEqual(sorted(actual), PLAN)

    def test_coverage_fan_in_rejects_missing_report(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        job = workflow.split('  dotnet-foundation-tests:', 1)[1].split('  server-tests:', 1)[0]
        self.assertIn('name: .NET Foundation Tests\n', job)
        self.assertIn('needs: [changes, dotnet-foundation-family-tests]', job)
        self.assertIn('pattern: foundation-coverage-*', job)
        step = job.split('      - name: Verify merged foundation results\n        run: |\n', 1)[1].split('\n      # Republish', 1)[0]
        script = '\n'.join(line[10:] for line in step.splitlines())
        script = script.replace("'${{ needs.dotnet-foundation-family-tests.result }}'", "'success'")
        with tempfile.TemporaryDirectory() as temp:
            temp = Path(temp)
            (temp / 'scripts/ci').mkdir(parents=True)
            (temp / 'scripts/ci/run-foundation-family.sh').symlink_to(RUNNER)
            results = temp / 'tests/TestResults'
            results.mkdir(parents=True)
            for index in range(len(PLAN)):
                (results / f'{index}.trx').touch()
            coverage = temp / 'tests/FoundationCoverage'
            coverage.mkdir(parents=True)
            env = dict(os.environ, GITHUB_STEP_SUMMARY=str(temp / 'summary'))
            def verify():
                return subprocess.run(['bash', '-e', '-o', 'pipefail', '-c', script], cwd=temp, env=env, capture_output=True)
            self.assertNotEqual(verify().returncode, 0)
            (coverage / 'coverage.cobertura.xml').write_text('<coverage/>')
            self.assertEqual(verify().returncode, 0)
            attachment = coverage / 'runner/In/runner'
            attachment.mkdir(parents=True)
            (attachment / 'coverage.cobertura.xml').write_text('<coverage/>')
            self.assertEqual(verify().returncode, 0)
            (results / '0.trx').unlink()
            self.assertNotEqual(verify().returncode, 0)

    def test_scoped_restore_build_and_failure_propagation(self):
        with tempfile.TemporaryDirectory() as temp:
            temp = Path(temp)
            stub = temp / 'dotnet'
            stub.write_text('''#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
args = sys.argv[1:]
entry = {'args': args, 'mysql': os.getenv('HONUA_TEST_MYSQL')}
if args[1].endswith('.slnf'):
    entry['projects'] = json.loads(Path(args[1]).read_text())['solution']['projects']
with open(os.environ['CALLS'], 'a') as f:
    f.write(json.dumps(entry) + '\\n')
sys.exit(1 if os.getenv('FAIL_PROJECT', 'never-match') in args[1] else 0)
''')
            stub.chmod(0o755)
            env = dict(os.environ, PATH=f'{temp}:{os.environ["PATH"]}',
                       CALLS=str(temp / 'calls'), FOUNDATION_RESULTS_DIR=str(temp / 'results'),
                       HONUA_RESTORE_MAX_ATTEMPTS='1')
            family = 'protocols-providers'
            specs = subprocess.check_output([RUNNER, 'list', family], text=True).splitlines()
            for command in ('restore', 'build', 'run'):
                subprocess.run([RUNNER, command, family], env=env, check=True, capture_output=True)
            calls = [json.loads(line) for line in (temp / 'calls').read_text().splitlines()]
            self.assertEqual(calls[0]['args'][0], 'restore')
            self.assertEqual(set(calls[0]['projects']), {s.split('|')[0] for s in specs})
            self.assertEqual(set(calls[1]['projects']), {s.split('|')[0] for s in specs if not s.endswith('advisory')})
            tests = [c for c in calls if c['args'][0] == 'test']
            self.assertEqual(len(tests), 18)
            for call, spec in zip(tests, specs):
                project, trx, filter_, envs, flags = spec.split('|')
                self.assertEqual(call['args'][1], project)
                self.assertIn('--no-build', call['args'])
                self.assertIn(f'trx;LogFileName={trx}.trx', call['args'])
                if filter_:
                    self.assertEqual(call['args'][call['args'].index('--filter') + 1], filter_)
                self.assertEqual(call['mysql'], '1' if envs else None)
            # A hard failure cannot disappear behind later successful projects.
            failed = subprocess.run([RUNNER, 'run', family], env=dict(env, FAIL_PROJECT='GeoServices'), capture_output=True)
            self.assertNotEqual(failed.returncode, 0)
            # Preserve the existing advisory build semantics.
            subprocess.run([RUNNER, 'run', family], env=dict(env, FAIL_PROJECT='Postgres.Security'), check=True, capture_output=True)
            # A failed restore must never be reported as success.
            failed = subprocess.run([RUNNER, 'restore', family], env=dict(env, FAIL_PROJECT='foundation-'), capture_output=True)
            self.assertNotEqual(failed.returncode, 0)


if __name__ == '__main__':
    unittest.main()
