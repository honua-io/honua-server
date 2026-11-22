#!/usr/bin/env python3
"""Fixture tests for scripts/ci/server-test-shard-free-disk.sh headroom guard."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / 'scripts/ci/server-test-shard-free-disk.sh'


class ServerTestShardFreeDiskTests(unittest.TestCase):
    def make_fake_bin(self, root: Path, df_script: str, sudo_sleep: str = '0') -> tuple[Path, Path]:
        tools = root / 'bin'
        tools.mkdir()
        sudo_calls = root / 'sudo.calls'

        df_bin = tools / 'df'
        df_bin.write_text('#!/usr/bin/env bash\n' + df_script + '\n', encoding='utf-8')
        df_bin.chmod(0o755)

        sudo_bin = tools / 'sudo'
        sudo_bin.write_text(
            '#!/usr/bin/env bash\n'
            f'if [[ "{sudo_sleep}" != "0" ]]; then sleep "{sudo_sleep}"; fi\n'
            'printf "%s\\n" "$*" >> "${FAKE_SUDO_CALLS}"\n',
            encoding='utf-8',
        )
        sudo_bin.chmod(0o755)
        return tools, sudo_calls

    def run_helper(self, root: Path, tools: Path, sudo_calls: Path, *args: str, **extra_env: str):
        env = dict(os.environ)
        env['PATH'] = str(tools) + os.pathsep + env.get('PATH', '')
        env['RUNNER_TEMP'] = str(root / 'temp')
        env['FAKE_SUDO_CALLS'] = str(sudo_calls)
        env.pop('HONUA_MIN_FREE_DISK_GB', None)
        env.pop('HONUA_FREE_DISK_PATH', None)
        env.update(extra_env)
        (root / 'temp').mkdir(exist_ok=True)
        bash = shutil.which('bash')
        self.assertIsNotNone(bash, 'bash is required for fixture tests')
        return subprocess.run(
            [bash, str(SCRIPT), *args],
            env=env,
            capture_output=True,
            text=True,
            check=False,
        )

    def test_skips_cleanup_when_free_space_meets_default_threshold(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            # 75 GiB available (75 * 1048576 = 78643200 KiB)
            df_script = (
                'if [[ "$1" == "-Pk" ]]; then\n'
                '  printf "Filesystem 1024-blocks Used Available Capacity Mounted on\\n"\n'
                '  printf "/dev/root 152043520 73400320 78643200 49%% /\\n"\n'
                'else\n'
                '  printf "Filesystem Size Used Avail Use%% Mounted on\\n"\n'
                '  printf "/dev/root 145G 70G 75G 49%% /\\n"\n'
                'fi'
            )
            tools, sudo_calls = self.make_fake_bin(root, df_script)
            # Pre-seed a stale PID file to verify skip removes it.
            (root / 'temp').mkdir()
            stale_pid = root / 'temp/server-test-shard-free-disk.pid'
            stale_pid.write_text('999999\n', encoding='utf-8')

            start = self.run_helper(root, tools, sudo_calls)
            self.assertEqual(start.returncode, 0, start.stderr)
            self.assertIn('Skipping server-test shard disk cleanup: 75 GiB free on / (>= 40 GiB threshold).', start.stdout)
            self.assertFalse(stale_pid.exists())
            self.assertFalse(sudo_calls.exists())

            await_res = self.run_helper(root, tools, sudo_calls, '--await')
            self.assertEqual(await_res.returncode, 0, await_res.stderr)
            self.assertIn('Skipping server-test shard disk cleanup: 75 GiB free on / (>= 40 GiB threshold).', await_res.stdout)
            self.assertIn('/dev/root 145G 70G 75G 49% /', await_res.stdout)

    def test_runs_background_cleanup_when_below_threshold_and_awaits_completion(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            # 39 GiB available (39 * 1048576 = 40894464 KiB)
            df_script = (
                'if [[ "$1" == "-Pk" ]]; then\n'
                '  printf "Filesystem 1024-blocks Used Available Capacity Mounted on\\n"\n'
                '  printf "/dev/root 75497472 34603008 40894464 46%% /\\n"\n'
                'else\n'
                '  printf "Filesystem Size Used Avail Use%% Mounted on\\n"\n'
                '  printf "/dev/root 72G 33G 39G 46%% /\\n"\n'
                'fi'
            )
            tools, sudo_calls = self.make_fake_bin(root, df_script, sudo_sleep='0.05')

            start = self.run_helper(root, tools, sudo_calls)
            self.assertEqual(start.returncode, 0, start.stderr)
            self.assertIn('Server-test shard disk cleanup started in the background', start.stdout)
            pid_file = root / 'temp/server-test-shard-free-disk.pid'
            self.assertTrue(pid_file.exists())

            await_res = self.run_helper(root, tools, sudo_calls, '--await')
            self.assertEqual(await_res.returncode, 0, await_res.stderr)
            self.assertIn('/dev/root 72G 33G 39G 46% /', await_res.stdout)
            self.assertFalse(pid_file.exists())
            self.assertEqual(
                sudo_calls.read_text(encoding='utf-8').splitlines(),
                [
                    'rm -rf /usr/local/lib/android',
                    'rm -rf /usr/share/swift',
                    'rm -rf /usr/local/.ghcup',
                    'rm -rf /opt/hostedtoolcache/CodeQL',
                ],
            )

    def test_fails_safe_to_cleanup_when_df_fails_or_returns_non_numeric(self):
        for label, df_script in (
            ('df-error', 'exit 1'),
            ('df-garbage', 'printf "Filesystem 1024-blocks Used Available\\n/dev/root 1 2 N/A\\n"'),
        ):
            with self.subTest(case=label), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                tools, sudo_calls = self.make_fake_bin(root, df_script)

                start = self.run_helper(root, tools, sudo_calls)
                self.assertEqual(start.returncode, 0, start.stderr)
                self.assertIn('started in the background', start.stdout)

                await_res = self.run_helper(root, tools, sudo_calls, '--await')
                self.assertEqual(await_res.returncode, 0, await_res.stderr)
                self.assertEqual(len(sudo_calls.read_text(encoding='utf-8').splitlines()), 4)

    def test_custom_threshold_and_invalid_threshold_fallback(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            # Exactly 40 GiB available (40 * 1048576 = 41943040 KiB)
            df_script = (
                'if [[ "$1" == "-Pk" ]]; then\n'
                '  printf "Filesystem 1024-blocks Used Available Capacity Mounted on\\n"\n'
                '  printf "/dev/root 104857600 62914560 41943040 60%% /\\n"\n'
                'else\n'
                '  printf "/dev/root 100G 60G 40G 60%% /\\n"\n'
                'fi'
            )
            tools, sudo_calls = self.make_fake_bin(root, df_script)

            # Invalid threshold falls back to 40 GiB -> 40 >= 40 skips cleanup.
            for bad_value in ('0', '-10', 'not-a-number'):
                with self.subTest(bad_value=bad_value):
                    res = self.run_helper(root, tools, sudo_calls, HONUA_MIN_FREE_DISK_GB=bad_value)
                    self.assertEqual(res.returncode, 0, res.stderr)
                    self.assertIn('(>= 40 GiB threshold)', res.stdout)
                    self.assertFalse(sudo_calls.exists())

            # Higher custom threshold (50 GiB) triggers cleanup when 40 GiB free.
            res = self.run_helper(root, tools, sudo_calls, HONUA_MIN_FREE_DISK_GB='50')
            self.assertEqual(res.returncode, 0, res.stderr)
            self.assertIn('started in the background', res.stdout)
            await_res = self.run_helper(root, tools, sudo_calls, '--await')
            self.assertEqual(await_res.returncode, 0, await_res.stderr)
            self.assertEqual(len(sudo_calls.read_text(encoding='utf-8').splitlines()), 4)


if __name__ == '__main__':
    unittest.main()
