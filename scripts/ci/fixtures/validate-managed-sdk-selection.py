#!/usr/bin/env python3
"""SDK rollover contracts using fake hosts; no SDK installation or .NET build."""
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[3]
ACTION = (ROOT / '.github/actions/setup-dotnet-ci/action.yml').read_text(encoding='utf-8')


def step_script(name):
    block = ACTION.split('    - name: ' + name + '\n', 1)[1].split('    - name:', 1)[0]
    script = re.search(r'      run: \|\n((?:        [^\n]*\n|\n)+)', block).group(1)
    return textwrap.dedent(script)


def job(source, name):
    return re.search(r'^  ' + re.escape(name) + r':\n(.*?)(?=^  [\w-]+:|\Z)',
                     source, re.M | re.S).group(1)


def bash_path(path):
    if os.name != 'nt':
        return str(path)
    shell = Path(shutil.which('bash'))
    cygpath = shell.parent.parent / 'usr/bin/cygpath.exe'
    return subprocess.check_output([str(cygpath), '-u', str(path)], text=True).strip()


class ManagedSdkSelectionTests(unittest.TestCase):
    def run_script(self, name, directory, **environment):
        env = dict(os.environ, **environment)
        script = directory / 'sdk-command.sh'
        script.write_bytes(step_script(name).encode('utf-8'))
        # Resolve PATH explicitly on Windows instead of System32's WSL launcher.
        shell = shutil.which('bash')
        if shell is None:
            self.fail('The workflow fixtures require Bash on PATH.')
        for key in ('RUNNER_TEMP', 'GITHUB_ENV', 'GITHUB_OUTPUT', 'DOTNET_INSTALL_DIR', 'DOTNET_ROOT'):
            if key in environment:
                env[key] = bash_path(environment[key])
        return subprocess.run([shell, '-e', script.as_posix()], env=env,
                              capture_output=True, text=True)

    def fake_host(self, folder, version):
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / 'dotnet'
        path.write_bytes(('#!/usr/bin/env bash\nprintf "%s\\n" "' + version + '"\n').encode('utf-8'))
        path.chmod(0o755)

    def test_exact_pin_uses_private_install_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            env_file = root / 'job.env'
            result = self.run_script('Select exact managed producer SDK', root,
                EXPECTED_DOTNET_SDK='10.0.401', RUNNER_TEMP=root.as_posix(), GITHUB_ENV=env_file.as_posix())
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(env_file.read_text(), 'DOTNET_INSTALL_DIR=' + bash_path(root) + '/honua-managed-sdk/10.0.401\n')
            self.assertFalse((root / 'global.json').exists())

    def test_float_or_path_cannot_be_a_producer_sdk_pin(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for value in ('10.0.x', '../foreign', '10.0.401\nINJECTED=value', ''):
                result = self.run_script('Select exact managed producer SDK', root,
                    EXPECTED_DOTNET_SDK=value, RUNNER_TEMP=root.as_posix(), GITHUB_ENV=(root / 'job.env').as_posix())
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse((root / 'job.env').exists())

    def test_newer_host_is_rejected_instead_of_spoofing_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fake_host(root / 'newer-host', '10.0.402')
            output = root / 'output'
            result = self.run_script('Record actual SDK identity', root,
                EXPECTED_DOTNET_SDK='10.0.401', GITHUB_OUTPUT=output.as_posix(),
                DOTNET_INSTALL_DIR=(root / 'private-host').as_posix(), DOTNET_ROOT=(root / 'private-host').as_posix(),
                PATH=(root / 'newer-host').as_posix() + os.pathsep + os.environ['PATH'])
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse(output.exists())
            self.assertIn('differs from managed producer', result.stderr)

    def test_private_producer_host_wins_over_newer_preinstalled_sdk(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fake_host(root / 'newer-host', '10.0.402')
            self.fake_host(root / 'private-host', '10.0.401')
            output = root / 'output'
            result = self.run_script('Record actual SDK identity', root,
                EXPECTED_DOTNET_SDK='10.0.401', GITHUB_OUTPUT=output.as_posix(),
                DOTNET_INSTALL_DIR=(root / 'private-host').as_posix(), DOTNET_ROOT=(root / 'private-host').as_posix(),
                PATH=os.pathsep.join(((root / 'private-host').as_posix(),
                                     (root / 'newer-host').as_posix(), os.environ['PATH'])))
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(output.read_text(), 'version=10.0.401\n')

    def test_unpinned_producer_exports_actual_selected_version(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fake_host(root / 'host', '10.0.402')
            result = self.run_script('Record actual SDK identity', root,
                EXPECTED_DOTNET_SDK='', GITHUB_OUTPUT=(root / 'output').as_posix(),
                PATH=(root / 'host').as_posix() + os.pathsep + os.environ['PATH'])
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual((root / 'output').read_text(), 'version=10.0.402\n')

    def test_all_managed_consumers_use_their_actual_producer_sdk(self):
        for workflow, producer, output, consumers in (
            ('pr-gate.yml', 'pr-gate', 'managed-sdk-version', ('pr-gate-tests', 'affected-shard')),
            ('ci.yml', 'build', 'managed_sdk_version', ('dotnet-foundation-family-tests', 'server-tests')),
        ):
            source = (ROOT / '.github/workflows' / workflow).read_text(encoding='utf-8')
            body = job(source, producer)
            self.assertIn(output + ': ${{ steps.managed-sdk.outputs.dotnet-sdk-version }}', body)
            self.assertIn('id: managed-sdk', body)
            for consumer in consumers:
                body = job(source, consumer)
                self.assertIn('artifact-sdk-version: ${{ needs.' + producer + '.outputs.' + output + ' }}', body)
                self.assertLess(body.index('artifact-sdk-version:'), body.index('mode: consume'))
        self.assertIn('value: ${{ steps.sdk-identity.outputs.version }}', ACTION)
        self.assertNotIn('HONUA_SERVER_TEST_ARTIFACT_DOTNET_SDK', ACTION)

    def test_same_version_from_a_foreign_host_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fake_host(root / 'foreign-host', '10.0.401')
            (root / 'private-host').mkdir()
            result = self.run_script('Record actual SDK identity', root,
                EXPECTED_DOTNET_SDK='10.0.401', GITHUB_OUTPUT=(root / 'output').as_posix(),
                DOTNET_INSTALL_DIR=(root / 'private-host').as_posix(), DOTNET_ROOT=(root / 'private-host').as_posix(),
                PATH=(root / 'foreign-host').as_posix() + os.pathsep + os.environ['PATH'])
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('host/runtime paths differ', result.stderr)
            self.assertFalse((root / 'output').exists())


if __name__ == '__main__':
    unittest.main()
