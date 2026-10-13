#!/usr/bin/env python3
"""Exercise restore targets and advisory failures with a fake dotnet only."""
import json
import os
from pathlib import Path
import shutil
import textwrap
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]


class ScopedRestoreTests(unittest.TestCase):
    def test_lean_restore_scope_and_format(self):
        action = (ROOT / '.github/actions/lean-gate/action.yml').read_text()
        section = action.split('    - name: Restore selected build scope\n', 1)[1].split('    # Composite-action', 1)[0]
        restore_run = textwrap.dedent(section.split('      run: |\n', 1)[1]).rstrip()
        self.assertLess(action.index('    - name: Compute gate build scope'), action.index('    - name: Restore selected build scope'))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'scripts/ci').mkdir(parents=True)
            helper = root / 'scripts/ci/dotnet-restore-retry.sh'
            helper.write_text('#!/bin/bash\nprintf "%s\\n" "$@" > target\n')
            helper.chmod(0o755)
            (root / 'selected.slnf').write_text('{}')
            for mode, formatting, expected in [('affected','false','selected.slnf'), ('affected','true','Honua.sln'), ('full','false','Honua.sln'), ('unknown','false','Honua.sln')]:
                env = dict(os.environ, GATE_BUILD_MODE=mode, GATE_RUN_FORMAT=formatting, GATE_BUILD_FILTER='selected.slnf')
                subprocess.run(['bash','-e','-c',restore_run],cwd=root,env=env,check=True)
                self.assertEqual((root / 'target').read_text().splitlines()[0], expected)
            (root / 'selected.slnf').unlink()
            self.assertNotEqual(subprocess.run(['bash','-e','-c',restore_run],cwd=root,env=dict(os.environ,GATE_BUILD_MODE='affected',GATE_RUN_FORMAT='false',GATE_BUILD_FILTER='selected.slnf'),capture_output=True).returncode, 0)

    def test_phase_validation_and_producer_union(self):
        action = (ROOT / '.github/actions/lean-gate/action.yml').read_text()
        phase = action.split('    - name: Validate gate phase\n', 1)[1].split('    #', 1)[0]
        phase_run = textwrap.dedent(phase.split('      run: |\n', 1)[1]).rstrip()
        for value in ['all', 'build', 'test', 'invalid']:
            result = subprocess.run(['bash', '-e', '-c', phase_run], env=dict(os.environ, GATE_PHASE=value), capture_output=True)
            self.assertEqual(result.returncode == 0, value != 'invalid')
        section = action.split('    - name: Union producer test roots\n', 1)[1].split('    - name:', 1)[0]
        script = textwrap.dedent(section.split('      run: |\n', 1)[1]).rstrip()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'scripts/ci').mkdir(parents=True)
            (root / 'scripts/ci/managed-test-build.py').write_text('import sys, json\nopen("union-call", "w").write(json.dumps(sys.argv[1:]))\n')
            env = dict(os.environ, GATE_BUILD_MODE='affected', GATE_BUILD_FILTER='selected.slnf', PRODUCER_PROJECTS_FILE='roots.json', GITHUB_OUTPUT=str(root / 'output'))
            subprocess.run(['bash', '-e', '-c', script], cwd=root, env=env, check=True)
            self.assertEqual(json.loads((root / 'union-call').read_text()), ['filter', '--projects-file', 'roots.json', '--base-filter', 'selected.slnf', '--output', 'selected.slnf'])
            self.assertIn('filter=selected.slnf', (root / 'output').read_text())
            (root / 'union-call').unlink()
            subprocess.run(['bash', '-e', '-c', script], cwd=root, env=dict(env, GATE_BUILD_MODE='full', GATE_BUILD_FILTER=''), check=True)
            self.assertFalse((root / 'union-call').exists())

    def test_foundation_same_filter_and_advisory_restore_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'scripts/ci').mkdir(parents=True)
            for name in ['run-foundation-family.sh', 'dotnet-restore-retry.sh']:
                shutil.copy2(ROOT / 'scripts/ci' / name, root / 'scripts/ci' / name)
            (root / 'Honua.sln').touch()
            fake = root / 'dotnet'
            fake.write_text('#!/bin/bash\nprintf "%s\\n" "$*" >> "$CALLS"\nif [[ "$1" == restore && ( "$2" == *Postgres.Security* || "${FAIL_REQUIRED:-}" == 1 ) ]]; then exit 1; fi\n')
            fake.chmod(0o755)
            env = dict(os.environ, PATH=str(root)+':'+os.environ['PATH'], CALLS=str(root / 'calls'), HONUA_RESTORE_MAX_ATTEMPTS='1')
            def run(*args):
                return subprocess.run(['bash','scripts/ci/run-foundation-family.sh',*args],cwd=root,env=env,capture_output=True,text=True,check=True)
            run('filter','providers','selected.slnf')
            projects=json.loads((root / 'selected.slnf').read_text())['solution']['projects']
            self.assertFalse(any('Postgres.Security' in p for p in projects))
            bad = subprocess.run(['bash','scripts/ci/run-foundation-family.sh','filter','unknown','bad.slnf'],cwd=root,env=env,capture_output=True)
            self.assertNotEqual(bad.returncode, 0)
            run('restore','providers','selected.slnf'); run('build','providers','selected.slnf')
            calls=(root / 'calls').read_text().splitlines()
            self.assertTrue(calls[0].startswith('restore selected.slnf '))
            self.assertTrue(calls[1].startswith('build selected.slnf '))
            failed = subprocess.run(['bash','scripts/ci/run-foundation-family.sh','restore','providers','selected.slnf'],cwd=root,env=dict(env,FAIL_REQUIRED='1'),capture_output=True)
            self.assertNotEqual(failed.returncode, 0)
            run('run','providers')
            calls=(root / 'calls').read_text().splitlines()
            self.assertTrue(any(c.startswith('restore ') and 'Postgres.Security' in c for c in calls))
            self.assertFalse(any(c.startswith(('build ','test ')) and 'Postgres.Security' in c for c in calls))
            self.assertTrue(any(c.startswith('test ') and 'MySql' in c for c in calls))


if __name__ == '__main__':
    unittest.main()
