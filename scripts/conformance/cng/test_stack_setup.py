"""Exercise the real setup function with failing process boundaries, without Docker."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

RUNNER = Path(__file__).with_name('run-cng-conformance.sh')


class StackSetupTests(unittest.TestCase):
    def run_setup(self, fail_at=0, skip=False):
        source = RUNNER.read_text()
        function = source[source.index('bring_up_stack() {'):source.index('# --- Validators')]
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / 'calls'
            # The mock fails one process and records all later calls, so continued
            # setup cannot hide a failed build, migration, seed or restart.
            script = '''
set -uo pipefail
COMPOSE_CMD=compose
CNG_COMPOSE_ARGS=()
YELLOW= GREEN= NC=
BASE_URL=unused CNG_SEED_FILE=seed SERVER_HEALTH_TIMEOUT=0
GITHUB_PACKAGES_ACTOR=test GITHUB_PACKAGES_TOKEN=placeholder
compose() { record "compose $*"; }
docker() { record "docker $*"; }
wait_for_health() { record "health $*"; }
record() {
    echo "$*" >> "$CALL_LOG"
    local count
    count=$(wc -l < "$CALL_LOG")
    if [[ "$count" == "$FAIL_AT" ]]; then return 9; fi
    if [[ "$*" == "compose ps -q postgres" ]]; then echo postgres-id; fi
}
'''
            result = subprocess.run(['bash', '-c', script + function + '\nif ! bring_up_stack; then exit 42; fi\n'],
                env={**os.environ, 'CALL_LOG': str(log), 'FAIL_AT': str(fail_at),
                     'SKIP_BUILD': str(skip).lower()}, capture_output=True, text=True, timeout=10)
            return result, log.read_text().splitlines()

    def test_failure_stops_before_any_dependent_action(self):
        _, baseline = self.run_setup()
        for index, command in enumerate(baseline, 1):
            if command.startswith('compose down'):
                continue  # Best-effort removal of a previous ephemeral stack.
            with self.subTest(command=command):
                result, calls = self.run_setup(fail_at=index)
                self.assertEqual(42, result.returncode, result.stderr)
                self.assertEqual(baseline[:index], calls)

    def test_success_runs_seed_and_restart(self):
        result, calls = self.run_setup()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('docker cp seed postgres-id:/tmp/cng-seed.sql', calls)
        self.assertEqual('health honua-server 0', calls[-1])

    def test_published_image_skips_build(self):
        result, calls = self.run_setup(skip=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(any('build honua-server' in call for call in calls))
