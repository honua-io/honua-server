#!/usr/bin/env python3
"""Host NuGet fallback folder contracts: the real setup step against fake host folders."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[3]
ACTION = (ROOT / '.github/actions/setup-dotnet-ci/action.yml').read_text(encoding='utf-8')
WORKFLOW = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
PR_GATE = (ROOT / '.github/workflows/pr-gate.yml').read_text(encoding='utf-8')
STEP = 'Use host NuGet fallback folder'


def step_block(name):
    return ACTION.split('    - name: ' + name + '\n', 1)[1].split('    - name:', 1)[0]


def job(name, workflow=WORKFLOW):
    return re.search(r'^  ' + re.escape(name) + r':\n(.*?)(?=^  [\w-]+:\n|\Z)',
                     workflow, re.M | re.S).group(1)


@unittest.skipIf(os.name == 'nt' or os.geteuid() == 0, 'needs POSIX permissions as a non-root user')
class HostFallbackStepTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.env_file = self.root / 'job.env'
        self.output = self.root / 'output'
        script = re.search(r'      run: \|\n((?:        [^\n]*\n|\n)+)', step_block(STEP)).group(1)
        self.script = self.root / 'step.sh'
        self.script.write_text(textwrap.dedent(script))

    def writable_generation(self, name, packages=1):
        folder = self.root / name
        for index in range(packages):
            package = folder / f'package.{index}' / '1.0.0'
            package.mkdir(parents=True)
            (package / '.nupkg.metadata').touch()
        folder.mkdir(exist_ok=True)
        return folder

    def generation(self, name='gen-a', packages=1):
        folder = self.writable_generation(name, packages)
        folder.chmod(0o555)
        self.addCleanup(folder.chmod, 0o755)
        return folder

    def run_step(self, folder):
        env = dict(os.environ, GITHUB_ENV=str(self.env_file), GITHUB_OUTPUT=str(self.output))
        env.pop('HONUA_NUGET_FALLBACK', None)
        if folder is not None:
            env['HONUA_NUGET_FALLBACK'] = str(folder)
        result = subprocess.run(['bash', '-e', '-o', 'pipefail', str(self.script)], env=env,
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result

    def assert_not_used(self):
        self.assertFalse(self.env_file.exists())
        self.assertFalse(self.output.exists())

    def test_read_only_generation_is_pinned_through_the_link(self):
        generation = self.generation()
        (self.root / 'current').symlink_to(generation.name)
        self.run_step(self.root / 'current')
        self.assertEqual(self.env_file.read_text(), f'NUGET_FALLBACK_PACKAGES={generation}\n')
        self.assertEqual(self.output.read_text(), 'used=true\n')

    def test_runner_without_a_folder_keeps_the_package_cache(self):
        self.run_step(None)
        self.assert_not_used()

    def test_missing_writable_or_empty_folders_keep_the_package_cache(self):
        for folder in (self.root / 'absent', self.writable_generation('gen-w'),
                       self.generation('gen-e', packages=0)):
            result = self.run_step(folder)
            self.assertIn('restoring the package cache', result.stdout)
            self.assert_not_used()


class HostFallbackWiringTests(unittest.TestCase):
    def test_fallback_is_opt_in_and_replaces_both_cache_steps(self):
        self.assertIn("if: inputs.host-package-fallback == 'true'", step_block(STEP))
        self.assertIn("default: 'false'", ACTION.split('  host-package-fallback:\n', 1)[1].split('\n\n', 1)[0])
        for name in ('Cache NuGet packages (trunk read/write)', 'Restore NuGet packages (non-trunk read-only)'):
            self.assertLess(ACTION.index('    - name: ' + STEP + '\n'), ACTION.index('    - name: ' + name + '\n'))
            self.assertIn("steps.nuget-fallback.outputs.used != 'true'", step_block(name).splitlines()[0])

    def test_fully_consumed_shards_recheck_package_imports_on_the_fallback(self):
        self.assertIn("value: ${{ steps.nuget-fallback.outputs.used == 'true' }}", ACTION)
        shard = job('server-tests')
        setup = shard.split('      - name: Setup .NET\n', 1)[1].split('      - name:', 1)[0]
        self.assertIn('id: setup-dotnet', setup)
        build = shard.split('      - name: Build server test binaries\n', 1)[1].split('      - name:', 1)[0]
        self.assertIn("steps.setup-dotnet.outputs.host-package-fallback-used == 'true'", build)
        self.assertIn('managed-test-build.py fallback', build)

    def test_cached_shard_binaries_recheck_package_imports_on_the_fallback(self):
        shard = job('server-tests')
        recheck = shard.split('      - name: Re-check package imports of cached shard binaries\n', 1)[1].split('      - name:', 1)[0]
        self.assertIn("steps.setup-dotnet.outputs.host-package-fallback-used == 'true'", recheck)
        self.assertIn("steps.shard-cache-materialize.outputs.restored == 'true'", recheck)
        restored_everything = ('--projects-file "${RUNNER_TEMP}/shard-projects.json" '
                               '--restored-file "${RUNNER_TEMP}/shard-projects.json"')
        self.assertIn('managed-test-build.py fallback ' + restored_everything, recheck)
        self.assertLess(shard.index('Verify and unpack exact-head shard cache'), shard.index('Re-check package imports'))
        self.assertLess(shard.index('Re-check package imports'), shard.index('      - name: Run server test shard'))

    def test_only_consumers_that_recheck_package_imports_opt_in(self):
        self.assertIn("host-package-fallback: 'true'", job('server-tests'))
        self.assertEqual(WORKFLOW.count('host-package-fallback:'), 1)
        affected = job('affected-shard', PR_GATE)
        self.assertIn("host-package-fallback: 'true'", affected)
        build = affected.split('      - name: Build missing managed test outputs\n', 1)[1].split('      - name:', 1)[0]
        self.assertNotIn('if:', build)
        self.assertIn('managed-test-build.py fallback', build)
        self.assertEqual(PR_GATE.count('host-package-fallback:'), 1)


if __name__ == '__main__':
    unittest.main()
