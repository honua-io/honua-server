#!/usr/bin/env python3
"""Prove worker bounds, collected failures, argv fidelity and CI authority."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
LIBRARY = ROOT / 'scripts/ci/lib/router-fixtures.sh'


class RouterFixturesTests(unittest.TestCase):
    def run_shell(self, body, *, ci='', workers='2', arguments=()):
        environment = dict(os.environ, CI=ci, GITHUB_ACTIONS='', HONUA_ROUTER_WORKERS=workers)
        return subprocess.run(
            ['bash', '-c', 'set -euo pipefail\n'
             'SCRIPT_DIR="$1/scripts/ci"\nPYTHON_BIN="$2"\n'
             'source "$SCRIPT_DIR/lib/router-fixtures.sh"\nshift 2\n'
             'router_fixtures_init "$@"\n' + body,
             'fixture', str(ROOT), sys.executable, *arguments],
            cwd=ROOT, env=environment, text=True, capture_output=True)

    def test_collects_every_failure_and_preserves_arguments(self):
        result = self.run_shell('''
run_fixture first bash -c 'echo first-failed; exit 9'
run_fixture second bash -c 'echo second-failed; exit 4'
run_fixture argv bash -c '[[ "$1" == "space * literal" ]]' bash 'space * literal'
run_fixture last printf '%s\\n' last-ran
router_fixtures_finish
''')
        self.assertNotEqual(result.returncode, 0)
        for expected in ['first-failed', 'second-failed', 'last-ran', '4 executed, 2 failed', 'fixture 0']:
            self.assertIn(expected, result.stdout)

    def test_function_failure_is_not_hidden_by_wrapper(self):
        result = self.run_shell('''
failing() { false; echo incorrectly-continued; }
run_fixture function failing
router_fixtures_finish
''')
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn('incorrectly-continued', result.stdout)
        self.assertIn('1 failed', result.stdout)

    def test_worker_limit(self):
        with tempfile.TemporaryDirectory() as directory:
            # A directory is an atomic lock; overlap would fail with one worker.
            body = '\n'.join(
                f'run_fixture worker-{index} bash -c \'set -e; mkdir "$1/lock"; sleep 0.05; rmdir "$1/lock"\' bash "{directory}"'
                for index in range(5))
            result = self.run_shell(body + '\nrouter_fixtures_finish', workers='1')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('5 executed, 0 failed', result.stdout)

    def test_ci_cannot_select_changed_only(self):
        result = self.run_shell('''
run_fixture routing:authority printf '%s\\n' authority-ran
router_fixtures_finish
''', ci='true', arguments=('--changed-only',))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('Router full validation', result.stdout)
        self.assertIn('authority-ran', result.stdout)

    def test_only_complete_input_closure_can_skip(self):
        result = self.run_shell('''
ROUTER_CHANGED_INPUTS=('.github/workflows/unrelated.yml')
run_fixture routing:unaffected false
run_fixture undeclared true
ROUTER_CHANGED_INPUTS=('scripts/ci/lib/jq-cr-safe.sh')
run_fixture routing:affected true
router_fixtures_finish
''')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('fixture SKIP routing:unaffected', result.stdout)
        self.assertIn('2 executed, 0 failed', result.stdout)

    def test_invalid_arguments_rejected(self):
        self.assertEqual(self.run_shell('', arguments=('--typo',)).returncode, 2)
        self.assertEqual(self.run_shell('', workers='0').returncode, 2)


class ChangedInputsTests(unittest.TestCase):
    def test_missing_base_selects_everything(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run([sys.executable, str(ROOT / 'scripts/ci/router-fixture-inputs.py')],
                                    cwd=directory, text=True, capture_output=True, check=True)
        self.assertEqual(result.stdout.strip(), '*')

    def test_rename_staged_unstaged_and_untracked_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def git(*args):
                subprocess.run(['git', *args], cwd=root, check=True, capture_output=True)
            git('init', '-q')
            git('config', 'user.name', 'fixture')
            git('config', 'user.email', 'fixture@example.invalid')
            (root / 'old').write_text('original')
            (root / 'unstaged').write_text('original')
            git('add', '.')
            git('commit', '-qm', 'base')
            git('update-ref', 'refs/remotes/origin/trunk', 'HEAD')
            git('mv', 'old', 'new')
            (root / 'unstaged').write_text('modified')
            (root / 'untracked').write_text('new')
            result = subprocess.run([sys.executable, str(ROOT / 'scripts/ci/router-fixture-inputs.py')],
                                    cwd=root, text=True, capture_output=True, check=True)
            self.assertEqual(set(result.stdout.splitlines()), {'old', 'new', 'unstaged', 'untracked'})
            (root / 'newline\nname').write_text('ambiguous')
            result = subprocess.run([sys.executable, str(ROOT / 'scripts/ci/router-fixture-inputs.py')],
                                    cwd=root, text=True, capture_output=True, check=True)
            self.assertEqual(result.stdout.strip(), '*')


if __name__ == '__main__':
    unittest.main()
