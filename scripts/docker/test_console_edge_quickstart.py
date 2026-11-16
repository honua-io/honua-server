"""Console in the customer package: the HTTPS edge, its Compose service and its settings."""
import ipaddress
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from compose_quickstart import initialize, read_secret
from package_quickstart import CONSOLE_WEB_IMAGE
from quickstart_testing import SERVER, QuickstartFixture


class ConsoleEdgeTests(QuickstartFixture, unittest.TestCase):
    def test_console_edge_is_read_only_and_serves_the_local_bundle(self):
        with tempfile.TemporaryDirectory() as directory:
            path, rendered, _ = self.packaged_console_install(Path(directory))
            console = rendered['services']['console']
            self.assertEqual(CONSOLE_WEB_IMAGE, console['image'])
            port = console['ports'][0]
            self.assertEqual(('8443', '127.0.0.1', '18083'), (str(port['target']), port['host_ip'], str(port['published'])))
            mounts = {volume['target']: volume for volume in console['volumes']}
            self.assertEqual(str(path / 'console'), mounts['/srv/console']['source'])
            self.assertEqual(str(path / 'console.config.json'), mounts['/etc/honua-console/config.json']['source'])
            self.assertTrue(all(volume.get('read_only') for volume in console['volumes']))
            self.assertEqual({'console_tls_certificate', 'console_tls_key'},
                             {secret['source'] for secret in console['secrets']})
            self.assertTrue(console['read_only'])
            self.assertEqual(['ALL'], console['cap_drop'])

    def test_server_trusts_forwarded_headers_only_from_the_edge(self):
        with tempfile.TemporaryDirectory() as directory:
            _, rendered, _ = self.packaged_console_install(Path(directory))
            services = rendered['services']
            self.assertEqual(['console-edge'], list(services['console']['networks']))
            edge = services['console']['networks']['console-edge']['ipv4_address']
            honua = services['honua']
            self.assertEqual({'default', 'console-edge'}, set(honua['networks']))
            server_edge = honua['networks']['console-edge']['ipv4_address']
            self.assertNotEqual(edge, server_edge, 'The server must never take the edge address')
            subnet = ipaddress.ip_network(rendered['networks']['console-edge']['ipam']['config'][0]['subnet'])
            self.assertTrue({ipaddress.ip_address(edge), ipaddress.ip_address(server_edge)} <= set(subnet.hosts()))
            self.assertEqual('true', honua['environment']['ForwardedHeaders__Enabled'])
            self.assertEqual(edge, honua['environment']['ForwardedHeaders__KnownProxies__0'])
            self.assertEqual('https://localhost:18083/admin/auth/callback', honua['environment']['Oidc__AdminAuthCallbackUri'])
            self.assertEqual('https://localhost:18083/console/', honua['environment']['Mcp__ApprovalConsoleUrl'])
            for name, service in services.items():
                if name not in ('console', 'honua'):
                    self.assertNotIn('console-edge', service.get('networks', {}), name)

    def test_console_profile_preserves_customer_and_executor_files(self):
        with tempfile.TemporaryDirectory() as directory:
            path, rendered, _ = self.packaged_console_install(Path(directory))
            services = rendered['services']
            self.assertIn('compose-manager', services)
            self.assertEqual(SERVER, services['honua']['image'])
            self.assertEqual('preserved', services['honua']['environment']['CUSTOMER_SETTING'])
            self.assertEqual('true', services['honua']['environment']['Studio__EndUserAuthorization__Enabled'])
            files = self.compose_env(path)['COMPOSE_FILE']
            # The overlay goes before customer files so their overrides win.
            self.assertEqual('compose.yaml:compose.executor.yaml:compose.console.yaml:customer.yaml:compose.executor.image.yaml', files)
            self.assertEqual(files, services['compose-executor']['environment']['ControlPlane__ComposeExecutor__ComposeFileList'])
            self.assertEqual(1, sum(line.startswith('COMPOSE_FILE=') for line in (path / '.env').read_text().splitlines()))
            self.assertNotIn(read_secret(path, 'HONUA_ADMIN_PASSWORD'), json.dumps(rendered))

    def test_console_files_settings_and_certificate(self):
        with tempfile.TemporaryDirectory() as directory:
            path, _, _ = self.packaged_console_install(Path(directory))
            modes = {name: (path / name).stat().st_mode & 0o777
                     for name in ('compose.console.yaml', 'console.nginx.conf', 'secrets/console_tls_key')}
            self.assertEqual({'compose.console.yaml': 0o640, 'console.nginx.conf': 0o644, 'secrets/console_tls_key': 0o640}, modes)
            edge_config = (path / 'console.nginx.conf').read_text()
            # Console loads ES modules; the stock MIME table would send them as octet-stream.
            self.assertIn(r'location ~ ^/console/.+\.(mjs|cjs)$ {', edge_config)
            self.assertIn('default_type text/javascript;', edge_config)
            self.assertEqual(dict(apiBaseUrl='/', v4Enabled=True,
                                  auth=dict(mode='builtin', authority=None, clientId=None, scopes=[])),
                             json.loads((path / 'console.config.json').read_text()))
            certificate = subprocess.run(['openssl', 'x509', '-in', str(path / 'secrets/console_tls_certificate'),
                                          '-noout', '-ext', 'subjectAltName'], capture_output=True, text=True)
            self.assertIn('DNS:localhost', certificate.stdout)
            self.assertIn('IP Address:127.0.0.1', certificate.stdout)

    def test_enabling_console_twice_preserves_the_install(self):
        with tempfile.TemporaryDirectory() as directory:
            path, _, _ = self.packaged_console_install(Path(directory))
            saved_env = (path / '.env').read_bytes()
            saved_key = (path / 'secrets/console_tls_key').read_bytes()

            initialize(path, ('console',))

            self.assertEqual(saved_env, (path / '.env').read_bytes())
            self.assertEqual(saved_key, (path / 'secrets/console_tls_key').read_bytes(), 'The local certificate must persist')

    def test_console_oidc_settings_reach_the_public_configuration_only(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare_console(Path(directory))
            env_file = path / '.env'
            env_file.write_text(env_file.read_text() + 'HONUA_CONSOLE_AUTH_MODE=oidc\n'
                                'HONUA_CONSOLE_OIDC_AUTHORITY=https://identity.example/realms/honua\n'
                                'HONUA_CONSOLE_OIDC_CLIENT_ID=honua-console\n')

            initialize(path)

            self.assertEqual(dict(mode='oidc', authority='https://identity.example/realms/honua',
                                  clientId='honua-console', scopes=['openid']),
                             json.loads((path / 'console.config.json').read_text())['auth'])

    def test_invalid_console_sign_in_settings_fail_before_changing_install(self):
        cases = ('HONUA_CONSOLE_AUTH_MODE=password\n',
                 'HONUA_CONSOLE_AUTH_MODE=oidc\nHONUA_CONSOLE_OIDC_CLIENT_ID=c\n',
                 'HONUA_CONSOLE_AUTH_MODE=oidc\nHONUA_CONSOLE_OIDC_AUTHORITY=http://identity.example\nHONUA_CONSOLE_OIDC_CLIENT_ID=c\n',
                 'HONUA_CONSOLE_AUTH_MODE=oidc\nHONUA_CONSOLE_OIDC_AUTHORITY=https://identity.example\n')
        for case in cases:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                path = self.prepare_console(Path(directory))
                env_file = path / '.env'
                env_file.write_text(env_file.read_text() + case)
                saved = env_file.read_bytes(), (path / 'console.config.json').read_bytes()

                with self.assertRaisesRegex(ValueError, 'HONUA_CONSOLE'):
                    initialize(path)

                self.assertEqual(saved, (env_file.read_bytes(), (path / 'console.config.json').read_bytes()))

    def test_console_compose_file_edit_survives_restart(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'install'
            self.package_console(path, self.console_bundle(Path(directory)))
            initialize(path)
            files = initialize(path, ('console',))['COMPOSE_FILE']
            env_file = path / '.env'
            self.assertEqual(1, sum(line.startswith('COMPOSE_FILE=') for line in env_file.read_text().splitlines()))
            edited = files.replace(':compose.executor.image.yaml', ':extra.yaml:compose.executor.image.yaml')
            env_file.write_text(env_file.read_text().replace(f'COMPOSE_FILE={files}', f'COMPOSE_FILE={edited}'))
            (path / 'extra.yaml').write_text('services:\n  honua:\n    environment:\n      EXTRA_SETTING: retained\n')

            self.assertEqual(edited, initialize(path)['COMPOSE_FILE'])
            rendered = subprocess.run(['docker', 'compose', 'config', '--format', 'json'],
                                     cwd=path, env=self.compose_env(path), capture_output=True, text=True)

            self.assertEqual(0, rendered.returncode)
            self.assertEqual('retained', json.loads(rendered.stdout)['services']['honua']['environment']['EXTRA_SETTING'])

    def test_console_activation_preserves_private_project_group(self):
        groups = [group for group in os.getgroups() if group != os.getegid()]
        if not groups:
            self.skipTest('A supplementary group is needed to test group preservation')
        project_group = groups[0]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'install'
            self.package_console(path, self.console_bundle(Path(directory)))
            initialize(path)
            os.chown(path, -1, project_group)
            env_file = path / '.env'
            os.chown(env_file, -1, project_group)
            saved = env_file.read_text()
            env_file.write_text(saved.replace(f'HONUA_PROJECT_GID={os.getegid()}', f'HONUA_PROJECT_GID={project_group}'))

            values = initialize(path, ('console',))

            self.assertEqual(str(project_group), values['HONUA_PROJECT_GID'])
            self.assertEqual(project_group, env_file.stat().st_gid)

    def test_console_without_packaged_bundle_fails_before_changing_install(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            saved = (path / '.env').read_bytes()

            with self.assertRaisesRegex(ValueError, 'released Console bundle'):
                initialize(path, ('console',))

            self.assertEqual(saved, (path / '.env').read_bytes())
            self.assertFalse((path / 'console.config.json').exists())

    def test_invalid_console_web_pin_fails_before_changing_install(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare_console(Path(directory))
            env_file = path / '.env'
            env_file.write_text('\n'.join(line for line in env_file.read_text().splitlines()
                                          if not line.startswith(('COMPOSE_PROFILES=', 'HONUA_CONSOLE_WEB_IMAGE='))) + '\n')
            saved = env_file.read_bytes()
            lock_file = path / 'images.json'
            lock = json.loads(lock_file.read_text())
            for pin in (None, 42, 'nginxinc/nginx-unprivileged:latest'):
                with self.subTest(pin=pin):
                    lock_file.write_text(json.dumps(dict(lock, consoleWeb=pin)))
                    with self.assertRaisesRegex(ValueError, 'immutable Console web image'):
                        initialize(path, ('console',))
                    self.assertEqual(saved, env_file.read_bytes())

    def test_saved_mutable_console_web_pin_is_rejected_on_restart(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare(directory)
            env_file = path / '.env'
            env_file.write_text(env_file.read_text() + 'HONUA_CONSOLE_WEB_IMAGE=nginxinc/nginx-unprivileged:latest\n')
            saved = env_file.read_bytes()

            with self.assertRaisesRegex(ValueError, 'HONUA_CONSOLE_WEB_IMAGE must be an immutable'):
                initialize(path)

            self.assertEqual(saved, env_file.read_bytes())


if __name__ == '__main__':
    unittest.main()
