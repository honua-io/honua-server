"""Customer package contract: immutable images, local secrets and executor isolation."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

from package_quickstart import package
from compose_quickstart import initialize

SERVER = 'ghcr.io/honua-io/honua-server@sha256:' + '1' * 64
EXECUTOR = 'ghcr.io/honua-io/honua-server@sha256:' + '2' * 64
WORKER = 'ghcr.io/honua-io/honua-worker@sha256:' + '3' * 64


class ComposeQuickstartTests(unittest.TestCase):
    def prepare(self, directory):
        path = Path(directory)
        package(path, SERVER, EXECUTOR, WORKER, '2026.1-rc.2')
        initialize(path)
        return path

    def test_private_keyring_can_be_initialized_with_only_required_capabilities(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            command = ['docker', 'compose']
            try:
                result = subprocess.run(command + ['run', '--rm', '--no-deps', 'keyring-init'],
                                        cwd=path, capture_output=True, text=True)
                self.assertEqual(0, result.returncode, 'Key-ring init must traverse the private install group')
                self.assertEqual(0o640, (path / 'secrets/keyring.pfx').stat().st_mode & 0o777)
                self.assertEqual(1001, (path / 'secrets/keyring.pfx').stat().st_gid)
            finally:
                subprocess.run(command + ['down'], cwd=path, capture_output=True)

    def test_package_starts_executor_by_default_with_separate_identity_and_scoped_mounts(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            result = subprocess.run(['docker', 'compose', '--project-directory', str(path),
                                     'config', '--format', 'json'], cwd=path, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, 'Package must render without exposing credentials')
            services = json.loads(result.stdout)['services']
            executor = services['compose-executor']
            self.assertEqual(EXECUTOR, executor['image'])
            self.assertEqual('1002:1001', executor['user'])
            self.assertTrue(executor['read_only'])
            self.assertEqual(['ALL'], executor['cap_drop'])
            self.assertFalse(executor.get('ports'))
            self.assertFalse(executor.get('profiles'))
            self.assertEqual('honua', executor['environment']['ControlPlane__ComposeExecutor__ServiceName'])
            self.assertEqual(str(path), executor['environment']['ControlPlane__ComposeExecutor__ProjectDirectory'])
            for name in ('honua', 'compose-manager'):
                self.assertFalse(any(v.get('source') == '/var/run/docker.sock'
                                     for v in services[name].get('volumes', [])))
            self.assertFalse(any(v['target'] == '/run/honua-compose' for v in services['honua']['volumes']))
            self.assertEqual(SERVER, services['compose-manager']['image'])
            self.assertEqual('true', services['compose-manager']['environment']['ControlPlane__DeployTargets__0__RequiresApproval'])
            self.assertNotEqual(services['honua']['environment']['HONUA_ADMIN_PASSWORD'],
                                services['compose-manager']['environment']['HONUA_ADMIN_PASSWORD'])
            self.assertEqual({'compose.yaml', 'compose.executor.yaml'},
                             {executor['environment'][f'ControlPlane__ComposeExecutor__ComposeFiles__{i}'] for i in (0, 1)})

    def test_credentials_are_local_distinct_and_stable_on_restart(self):
        with tempfile.TemporaryDirectory() as first, tempfile.TemporaryDirectory() as second:
            a, b = self.prepare(first), self.prepare(second)
            before = (a / '.env').read_bytes()
            initialize(a)
            self.assertEqual(before, (a / '.env').read_bytes())
            self.assertNotEqual(before, (b / '.env').read_bytes())
            self.assertEqual(0, (a / '.env').stat().st_mode & 0o007)
            self.assertEqual(0, (a / 'secrets/keyring.pfx').stat().st_mode & 0o007)
            values = dict(line.split('=', 1) for line in before.decode().splitlines())
            keys = ('POSTGRES_PASSWORD', 'HONUA_ADMIN_PASSWORD', 'HONUA_MANAGER_ADMIN_PASSWORD',
                    'HONUA_MANAGER_POSTGRES_PASSWORD', 'HONUA_MASTER_KEY', 'HONUA_MANAGER_MASTER_KEY',
                    'HONUA_KEYRING_PASSWORD')
            self.assertEqual(len(keys), len({values[key] for key in keys}))
            self.assertEqual(EXECUTOR, values['HONUA_EXECUTOR_IMAGE'])

    def test_mutable_image_references_are_rejected_before_writing_package(self):
        for images in (('ghcr.io/honua-io/honua-server:latest', EXECUTOR, WORKER),
                       (SERVER, 'ghcr.io/honua-io/honua-compose-executor:nightly', WORKER),
                       (SERVER, EXECUTOR, 'ghcr.io/honua-io/honua-worker:nightly')):
            with tempfile.TemporaryDirectory() as directory:
                with self.assertRaises(ValueError):
                    package(Path(directory), *images, '2026.1-rc.2')
                self.assertEqual([], list(Path(directory).iterdir()))


if __name__ == '__main__':
    unittest.main()
