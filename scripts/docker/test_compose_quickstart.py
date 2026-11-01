"""Customer package contract: immutable images, local secrets and executor isolation."""
import base64
import hmac
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

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

    def compose_env(self, path):
        values = dict(line.split('=', 1) for line in (path / '.env').read_text().splitlines())
        # The disposable project must win over ambient Compose flags, including cleanup.
        return dict(os.environ, **values)

    def test_audit_keys_are_local_persistent_and_bound_to_the_correct_process(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            values = dict(line.split('=', 1) for line in (path / '.env').read_text().splitlines())
            names = ('HONUA_AUDIT_CHAIN_KEY', 'HONUA_MANAGER_AUDIT_CHAIN_KEY')
            for name in names:
                self.assertTrue(name in values, 'Each process needs a locally initialized audit key')
                self.assertGreaterEqual(len(base64.b64decode(values[name], validate=True)), 32)
            self.assertFalse(hmac.compare_digest(values[names[0]], values[names[1]]))
            before = (path / '.env').read_bytes()
            initialize(path)
            self.assertTrue(hmac.compare_digest(before, (path / '.env').read_bytes()),
                            'Restart must preserve audit keys and existing credentials')
            result = subprocess.run(['docker', 'compose', 'config', '--format', 'json'],
                                    cwd=path, env=self.compose_env(path), capture_output=True, text=True)
            self.assertEqual(0, result.returncode, 'Audit keys must be wired into the packaged Compose install')
            services = json.loads(result.stdout)['services']
            for service, name in (('honua', names[0]), ('compose-manager', names[1])):
                self.assertTrue(hmac.compare_digest(values[name], services[service]['environment']['AuditLog__ChainVerification__Key']))
            self.assertTrue(hmac.compare_digest(values['HONUA_ADMIN_PASSWORD'],
                services['compose-executor']['environment']['ControlPlane__ComposeExecutor__VersionProbeApiKey']))

    def test_existing_invalid_audit_key_is_refused_without_replacing_credentials(self):
        for invalid in ('', 'invalid-value', base64.b64encode(bytes(16)).decode('ascii')):
            with self.subTest(case='invalid audit key'), tempfile.TemporaryDirectory() as directory:
                path = self.prepare(directory)
                env_file = path / '.env'
                lines = env_file.read_text().splitlines()
                env_file.write_text('\n'.join('HONUA_AUDIT_CHAIN_KEY=' + invalid if line.startswith('HONUA_AUDIT_CHAIN_KEY=') else line
                                              for line in lines) + '\n')
                before = env_file.read_bytes()
                with self.assertRaises(ValueError):
                    initialize(path)
                self.assertTrue(hmac.compare_digest(before, env_file.read_bytes()), 'Invalid saved keys must not be overwritten')

    def test_private_keyring_can_be_initialized_with_only_required_capabilities(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            command = ['docker', 'compose']
            try:
                result = subprocess.run(command + ['run', '--rm', '--no-deps', 'keyring-init'],
                                        cwd=path, env=self.compose_env(path), capture_output=True, text=True)
                self.assertEqual(0, result.returncode, 'Key-ring init must traverse the private install group')
                self.assertEqual(0o640, (path / 'secrets/keyring.pfx').stat().st_mode & 0o777)
                self.assertEqual(1001, (path / 'secrets/keyring.pfx').stat().st_gid)
            finally:
                subprocess.run(command + ['down'], cwd=path, env=self.compose_env(path), capture_output=True)

    def test_package_starts_executor_by_default_with_separate_identity_and_scoped_mounts(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            result = subprocess.run(['docker', 'compose', '--project-directory', str(path),
                                     'config', '--format', 'json'], cwd=path, env=self.compose_env(path), capture_output=True, text=True)
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
            self.assertFalse(hmac.compare_digest(services['honua']['environment']['HONUA_ADMIN_PASSWORD'],
                                                 services['compose-manager']['environment']['HONUA_ADMIN_PASSWORD']),
                             'Serving and management credentials must differ')
            self.assertEqual({'compose.yaml', 'compose.executor.yaml'},
                             {executor['environment'][f'ControlPlane__ComposeExecutor__ComposeFiles__{i}'] for i in (0, 1)})

    def test_credentials_are_local_distinct_and_stable_on_restart(self):
        with tempfile.TemporaryDirectory() as first, tempfile.TemporaryDirectory() as second:
            a, b = self.prepare(first), self.prepare(second)
            before = (a / '.env').read_bytes()
            initialize(a)
            self.assertTrue(hmac.compare_digest(before, (a / '.env').read_bytes()),
                            'Restart must preserve saved credentials')
            self.assertFalse(hmac.compare_digest(before, (b / '.env').read_bytes()),
                             'Installations must not share saved credentials')
            self.assertEqual(0, (a / '.env').stat().st_mode & 0o007)
            self.assertEqual(0, (a / 'secrets/keyring.pfx').stat().st_mode & 0o007)
            values = dict(line.split('=', 1) for line in before.decode().splitlines())
            keys = ('POSTGRES_PASSWORD', 'HONUA_ADMIN_PASSWORD', 'HONUA_MANAGER_ADMIN_PASSWORD',
                    'HONUA_MANAGER_POSTGRES_PASSWORD', 'HONUA_MASTER_KEY', 'HONUA_MANAGER_MASTER_KEY',
                    'HONUA_KEYRING_PASSWORD')
            self.assertEqual(len(keys), len({values[key] for key in keys}))
            self.assertEqual(EXECUTOR, values['HONUA_EXECUTOR_IMAGE'])

    def test_failed_certificate_export_can_be_retried(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            package(path, SERVER, EXECUTOR, WORKER, '2026.1-rc.2')
            run_openssl = subprocess.run

            def fail_export(command, **kwargs):
                if command[1] == 'pkcs12':
                    Path(command[command.index('-out') + 1]).write_bytes(b'partial')
                    return subprocess.CompletedProcess(command, 1)
                return run_openssl(command, **kwargs)

            with patch('compose_quickstart.subprocess.run', side_effect=fail_export):
                with self.assertRaises(RuntimeError):
                    initialize(path)
            self.assertFalse((path / 'secrets/keyring.pfx').exists(),
                             'Failed exports must not leave an apparently complete certificate')
            initialize(path)
            self.assertGreater((path / 'secrets/keyring.pfx').stat().st_size, 100)

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
