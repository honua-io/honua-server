"""Customer package contract: immutable images, local secrets and executor isolation."""
import base64
import hmac
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from package_quickstart import package
from compose_quickstart import ALL_SECRET_KEYS, initialize, read_secret

ROOT = Path(__file__).resolve().parents[2]

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
        return dict({k: v for k, v in os.environ.items() if k not in ALL_SECRET_KEYS}, **values)

    def secrets_of(self, path):
        return {key: read_secret(path, key) for key in ALL_SECRET_KEYS}

    def rc1_env(self, path):
        """The .env an rc.1 quickstart.py left behind: every secret is a Compose variable."""
        lines = (path / '.env').read_text().splitlines()
        lines += [f'{key}={value}' for key, value in self.secrets_of(path).items()]
        for key in ALL_SECRET_KEYS:
            (path / 'secrets' / key.lower()).unlink()
        (path / '.env').write_text('\n'.join(lines) + '\n')
        return dict(line.split('=', 1) for line in lines)

    def test_audit_keys_are_local_persistent_and_bound_to_the_correct_process(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            values = self.secrets_of(path)
            names = ('HONUA_AUDIT_CHAIN_KEY', 'HONUA_MANAGER_AUDIT_CHAIN_KEY')
            for name in names:
                self.assertGreaterEqual(len(base64.b64decode(values[name], validate=True)), 32)
            self.assertFalse(hmac.compare_digest(values[names[0]], values[names[1]]))
            before = (path / '.env').read_bytes()
            initialize(path)
            self.assertTrue(hmac.compare_digest(before, (path / '.env').read_bytes()),
                            'Restart must preserve settings')
            self.assertEqual(values, self.secrets_of(path), 'Restart must preserve audit keys and credentials')
            result = subprocess.run(['docker', 'compose', 'config', '--format', 'json'],
                                    cwd=path, env=self.compose_env(path), capture_output=True, text=True)
            self.assertEqual(0, result.returncode, 'Audit keys must be wired into the packaged Compose install')
            config = json.loads(result.stdout)
            services = config['services']
            for service, name in (('honua', names[0]), ('compose-manager', names[1])):
                self.assertEqual('/run/secrets/' + name.lower(),
                                 services[service]['environment']['HONUA_SECRET_FILE__AuditLog__ChainVerification__Key'])
                self.assertIn(name.lower(), [entry['source'] for entry in services[service]['secrets']])
                self.assertEqual(str(path / 'secrets' / name.lower()), config['secrets'][name.lower()]['file'])
            self.assertEqual('/run/secrets/honua_admin_password', services['compose-executor']['environment'][
                'HONUA_SECRET_FILE__ControlPlane__ComposeExecutor__VersionProbeApiKey'])
            self.assertEqual(['honua_admin_password'], [entry['source'] for entry in services['compose-executor']['secrets']])

    def test_compose_config_never_prints_a_secret(self):
        # Agents run these to inspect an install; none may reveal a credential.
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            env_text = (path / '.env').read_text()
            for key in ALL_SECRET_KEYS:
                self.assertNotIn(key + '=', env_text, 'Secrets must not be Compose interpolation variables')
                self.assertEqual(0o640, (path / 'secrets' / key.lower()).stat().st_mode & 0o777)
            values = self.secrets_of(path)
            for command in (['config'], ['config', '--environment'], ['config', '--format', 'json'],
                            ['config', '--no-interpolate'], ['config', '--variables']):
                with self.subTest(command=' '.join(command)):
                    result = subprocess.run(['docker', 'compose', *command], cwd=path, env=self.compose_env(path),
                                            capture_output=True, text=True)
                    self.assertEqual(0, result.returncode, 'The package must render')
                    output = result.stdout + result.stderr
                    leaked = [key for key, value in values.items() if value in output]
                    self.assertEqual([], leaked, 'docker compose config printed a secret')

    def test_entrypoint_loads_secret_files_and_refuses_missing_or_empty_ones(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            result = subprocess.run(['docker', 'compose', 'config', '--format', 'json'],
                                    cwd=path, env=self.compose_env(path), capture_output=True, text=True)
            services = json.loads(result.stdout)['services']
            for name in ('honua', 'compose-manager', 'compose-executor'):
                self.assertEqual(['bash', '-euc'], services[name]['entrypoint'][:2])
                self.assertEqual(['/app/Honua.Server'], services[name]['command'])
            entrypoint = services['honua']['entrypoint']
            self.assertEqual(entrypoint, services['compose-manager']['entrypoint'])
            # Rendered config re-escapes '$' as '$$'; the container receives single '$'.
            entrypoint = [part.replace('$$', '$') for part in entrypoint]
            files = Path(directory) / 'run'
            files.mkdir()
            (files / 'admin').write_text('admin-value')
            (files / 'db').write_text('db-value\n')
            env = {'PATH': os.environ['PATH'], 'ConnectionStrings__DefaultConnection': 'Host=postgres;Username=honua',
                   'HONUA_DATABASE_PASSWORD_FILE': str(files / 'db'),
                   'HONUA_SECRET_FILE__HONUA_ADMIN_PASSWORD': str(files / 'admin'),
                   'HONUA_SECRET_FILE__AuditLog__ChainVerification__Key': str(files / 'admin')}
            probe = [sys.executable, '-c', 'import json, os; print(json.dumps(dict(os.environ)))']
            loaded = subprocess.run([*entrypoint, *probe], env=env, capture_output=True, text=True)
            self.assertEqual(0, loaded.returncode)
            child = json.loads(loaded.stdout)
            self.assertEqual('admin-value', child['HONUA_ADMIN_PASSWORD'])
            self.assertEqual('admin-value', child['AuditLog__ChainVerification__Key'])
            self.assertEqual('Host=postgres;Username=honua;Password=db-value', child['ConnectionStrings__DefaultConnection'])
            self.assertFalse([name for name in child if 'SECRET_FILE' in name or 'PASSWORD_FILE' in name])
            (files / 'empty').write_text('')
            for broken in (files / 'missing', files / 'empty'):
                with self.subTest(case=broken.name):
                    refused = subprocess.run([*entrypoint, *probe], capture_output=True, text=True,
                                             env=dict(env, HONUA_SECRET_FILE__HONUA_MASTER_KEY=str(broken)))
                    self.assertNotEqual(0, refused.returncode, 'A missing or empty secret must stop startup')
                    self.assertEqual('', refused.stdout)

    def test_rc1_env_secrets_migrate_into_files_unchanged(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            pfx = (path / 'secrets/keyring.pfx').read_bytes()
            legacy = self.rc1_env(path)
            initialize(path)
            self.assertEqual({key: legacy[key] for key in ALL_SECRET_KEYS}, self.secrets_of(path),
                             'Migration must keep every secret value')
            migrated = (path / '.env').read_text()
            for key in ALL_SECRET_KEYS:
                self.assertNotIn(key + '=', migrated)
            settings = {key: value for key, value in legacy.items() if key not in ALL_SECRET_KEYS}
            self.assertEqual(settings, dict(line.split('=', 1) for line in migrated.splitlines()),
                             'Migration must keep every non-secret setting')
            self.assertEqual(pfx, (path / 'secrets/keyring.pfx').read_bytes())
            self.assertEqual(0o640, (path / '.env').stat().st_mode & 0o777)
            before = (path / '.env').read_bytes()
            initialize(path)
            self.assertEqual(before, (path / '.env').read_bytes(), 'Migration must be idempotent')
            self.assertEqual({key: legacy[key] for key in ALL_SECRET_KEYS}, self.secrets_of(path))

    def test_interrupted_migration_resumes_and_conflicts_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            legacy = self.rc1_env(path)
            # Files written but .env not yet rewritten: the next run finishes the migration.
            for key in ALL_SECRET_KEYS:
                (path / 'secrets' / key.lower()).write_text(legacy[key])
            initialize(path)
            self.assertNotIn('POSTGRES_PASSWORD=', (path / '.env').read_text())
            self.assertEqual(legacy['POSTGRES_PASSWORD'], read_secret(path, 'POSTGRES_PASSWORD'))
            (path / '.env').write_text((path / '.env').read_text() + 'POSTGRES_PASSWORD=' + 'f' * 64 + '\n')
            before = (path / '.env').read_bytes()
            with self.assertRaises(ValueError):
                initialize(path)
            self.assertEqual(before, (path / '.env').read_bytes(), 'A conflict must not change the install')
            self.assertEqual(legacy['POSTGRES_PASSWORD'], read_secret(path, 'POSTGRES_PASSWORD'))

    def test_migration_waits_for_compose_files_that_read_secret_files(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            legacy = self.rc1_env(path)
            # rc.1 compose files still interpolate the secrets; removing them from .env would break them.
            compose = (path / 'compose.yaml').read_text()
            (path / 'compose.yaml').write_text(compose.replace(
                'POSTGRES_PASSWORD_FILE: /run/secrets/postgres_password', 'POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?Required}'))
            before = (path / '.env').read_bytes()
            with self.assertRaises(ValueError):
                initialize(path)
            self.assertEqual(before, (path / '.env').read_bytes())
            self.assertFalse((path / 'secrets/postgres_password').exists())
            self.assertEqual(legacy['POSTGRES_PASSWORD'], dict(
                line.split('=', 1) for line in before.decode().splitlines())['POSTGRES_PASSWORD'])

    def test_existing_invalid_audit_key_is_refused_without_replacing_credentials(self):
        for invalid in ('', 'invalid-value', base64.b64encode(bytes(16)).decode('ascii')):
            for location in ('secrets file', 'rc.1 .env'):
                with self.subTest(case='invalid audit key', location=location), tempfile.TemporaryDirectory() as directory:
                    path = self.prepare(directory)
                    if location == 'rc.1 .env':
                        self.rc1_env(path)
                        env_file = path / '.env'
                        env_file.write_text(re.sub(r'(?m)^HONUA_AUDIT_CHAIN_KEY=.*$', 'HONUA_AUDIT_CHAIN_KEY=' + invalid,
                                                   env_file.read_text()))
                        saved = env_file
                    else:
                        saved = path / 'secrets/honua_audit_chain_key'
                        saved.write_text(invalid)
                    before = saved.read_bytes()
                    with self.assertRaises(ValueError):
                        initialize(path)
                    self.assertTrue(hmac.compare_digest(before, saved.read_bytes()), 'Invalid saved keys must not be overwritten')
                    if location == 'rc.1 .env':
                        self.assertFalse((path / 'secrets/postgres_password').exists(), 'Refusal must precede migration')

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
                for key in ALL_SECRET_KEYS:
                    secret = (path / 'secrets' / key.lower()).stat()
                    # The containers read through gid 1001; the installing user keeps ownership.
                    self.assertEqual((1001, 0o640, os.getuid()), (secret.st_gid, secret.st_mode & 0o777, secret.st_uid))
                self.assertTrue(read_secret(path, 'HONUA_MANAGER_ADMIN_PASSWORD'), 'The operator can still read it')
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
            self.assertEqual('/run/secrets/honua_admin_password',
                             services['honua']['environment']['HONUA_SECRET_FILE__HONUA_ADMIN_PASSWORD'])
            self.assertEqual('/run/secrets/honua_manager_admin_password',
                             services['compose-manager']['environment']['HONUA_SECRET_FILE__HONUA_ADMIN_PASSWORD'])
            self.assertFalse(hmac.compare_digest(read_secret(path, 'HONUA_ADMIN_PASSWORD'),
                                                 read_secret(path, 'HONUA_MANAGER_ADMIN_PASSWORD')),
                             'Serving and management credentials must differ')
            serving = {entry['source'] for entry in services['honua']['secrets']}
            self.assertFalse(serving & {entry['source'] for entry in services['compose-manager']['secrets']}
                             - {'honua_keyring_password'}, 'The serving container must not mount manager secrets')
            self.assertEqual({'compose.yaml', 'compose.executor.yaml'},
                             {executor['environment'][f'ControlPlane__ComposeExecutor__ComposeFiles__{i}'] for i in (0, 1)})

    def test_credentials_are_local_distinct_and_stable_on_restart(self):
        with tempfile.TemporaryDirectory() as first, tempfile.TemporaryDirectory() as second:
            a, b = self.prepare(first), self.prepare(second)
            before = self.secrets_of(a)
            initialize(a)
            self.assertEqual(before, self.secrets_of(a), 'Restart must preserve saved credentials')
            other = self.secrets_of(b)
            self.assertFalse(any(hmac.compare_digest(before[key], other[key]) for key in ALL_SECRET_KEYS),
                             'Installations must not share saved credentials')
            self.assertEqual(0, (a / '.env').stat().st_mode & 0o007)
            self.assertEqual(0, (a / 'secrets/keyring.pfx').stat().st_mode & 0o007)
            for key in ALL_SECRET_KEYS:
                self.assertEqual(0, (a / 'secrets' / key.lower()).stat().st_mode & 0o007)
            self.assertEqual(len(ALL_SECRET_KEYS), len(set(before.values())))
            values = dict(line.split('=', 1) for line in (a / '.env').read_text().splitlines())
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
