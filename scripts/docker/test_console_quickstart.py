"""Console in the customer package: upgrades, certificate renewal and edge restarts."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import compose_quickstart
from compose_quickstart import CONSOLE_RELOAD_MARKER, initialize
from package_quickstart import CONSOLE_WEB_IMAGE
from quickstart_testing import QuickstartFixture


class ConsoleLifecycleTests(QuickstartFixture, unittest.TestCase):
    def test_upgrade_from_server_hosted_console_converges_on_plain_restart(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare_console(Path(directory))
            env_file = path / '.env'
            # The earlier package saved a Console image pin and no web image.
            lines = [line for line in env_file.read_text().splitlines() if not line.startswith('HONUA_CONSOLE_WEB_IMAGE=')]
            lines.append('HONUA_CONSOLE_IMAGE=ghcr.io/honua-io/honua-console@sha256:' + '4' * 64)
            env_file.write_text('\n'.join(lines) + '\n')
            files = self.compose_env(path)['COMPOSE_FILE']

            values = initialize(path)

            self.assertEqual(CONSOLE_WEB_IMAGE, values['HONUA_CONSOLE_WEB_IMAGE'])
            self.assertNotIn('HONUA_CONSOLE_IMAGE', values)
            self.assertNotIn('HONUA_CONSOLE_IMAGE=', env_file.read_text())
            self.assertEqual(files, values['COMPOSE_FILE'])
            rendered = subprocess.run(['docker', 'compose', 'config', '--format', 'json'],
                                      cwd=path, env=self.compose_env(path), capture_output=True, text=True)
            self.assertEqual(0, rendered.returncode, rendered.stderr)
            self.assertEqual(CONSOLE_WEB_IMAGE, json.loads(rendered.stdout)['services']['console']['image'])

    def test_console_profile_keeps_customer_override_order(self):
        # Customer overrides listed after the Console overlay must keep winning on every run.
        customer = 'compose.yaml:compose.executor.yaml:compose.console.yaml:compose.customer.yaml:compose.executor.image.yaml'
        with patch.object(compose_quickstart, 'console_image_settings', return_value={}):
            unchanged = compose_quickstart.console_profile_settings(
                None, None, dict(COMPOSE_FILE=customer, COMPOSE_PROFILES='console'))
            added = compose_quickstart.console_profile_settings(
                None, None, dict(COMPOSE_FILE='compose.yaml:compose.executor.yaml:compose.customer.yaml:'
                                 'compose.executor.image.yaml'))
            bare = compose_quickstart.console_profile_settings(None, None, dict(COMPOSE_FILE='compose.yaml'))

        self.assertNotIn('COMPOSE_FILE', unchanged)
        self.assertEqual(customer, added['COMPOSE_FILE'])
        self.assertEqual('console', added['COMPOSE_PROFILES'])
        self.assertEqual('compose.yaml:compose.console.yaml', bare['COMPOSE_FILE'])

    def test_console_files_are_readable_by_the_web_server_after_a_private_extract(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare_console(Path(directory))
            for item in [path / 'console', *(path / 'console').rglob('*')]:
                item.chmod(0o750 if item.is_dir() else 0o640)

            initialize(path)

            for item in [path / 'console', *(path / 'console').rglob('*')]:
                self.assertEqual(0o755 if item.is_dir() else 0o644, item.stat().st_mode & 0o777, item)

    def test_console_certificate_is_an_end_entity_and_is_renewed_before_expiry(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare_console(Path(directory))
            certificate = path / 'secrets/console_tls_certificate'
            text = subprocess.run(['openssl', 'x509', '-in', str(certificate), '-noout', '-text'],
                                  capture_output=True, text=True).stdout
            self.assertIn('CA:FALSE', text)
            self.assertIn('TLS Web Server Authentication', text)
            subprocess.run(['openssl', 'req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:prime256v1',
                            '-nodes', '-subj', '/CN=localhost', '-days', '1', '-keyout', str(path / 'secrets/console_tls_key'),
                            '-out', str(certificate)], check=True, capture_output=True)
            expiring = certificate.read_bytes()
            marker = path / CONSOLE_RELOAD_MARKER

            initialize(path)

            self.assertNotEqual(expiring, certificate.read_bytes(), 'A certificate near expiry must be replaced')
            self.assertTrue(marker.exists(), 'The running edge must be recreated to serve it')
            initialize(path)
            self.assertTrue(marker.exists(), 'The restart stays pending until it succeeds')
            marker.unlink()
            initialize(path)
            self.assertFalse(marker.exists(), 'A current certificate and unchanged settings need no restart')

    def test_certificate_without_end_entity_constraint_is_reissued(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare_console(Path(directory))
            certificate = path / 'secrets/console_tls_certificate'
            subprocess.run(['openssl', 'req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:prime256v1',
                            '-nodes', '-subj', '/CN=localhost', '-days', '397', '-addext', 'basicConstraints=critical,CA:TRUE',
                            '-keyout', str(path / 'secrets/console_tls_key'), '-out', str(certificate)],
                           check=True, capture_output=True)
            initialize(path)

            text = subprocess.run(['openssl', 'x509', '-in', str(certificate), '-noout', '-text'],
                                  capture_output=True, text=True).stdout
            self.assertIn('CA:FALSE', text)
            self.assertTrue((path / CONSOLE_RELOAD_MARKER).exists())

    def test_first_console_install_needs_no_edge_restart(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'install'
            self.package_console(path, self.console_bundle(Path(directory)))
            initialize(path, ('console',))

            self.assertFalse((path / CONSOLE_RELOAD_MARKER).exists())

    def test_edge_restart_survives_a_failed_start_and_clears_after_success(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare_console(Path(directory))
            env_file = path / '.env'
            env_file.write_text(env_file.read_text() + 'HONUA_CONSOLE_V4=false\n')
            marker = path / CONSOLE_RELOAD_MARKER
            calls = []

            def compose(command, **_):
                calls.append(command)
                return outcomes.pop(0)

            argv = ['quickstart.py', '--directory', str(path)]
            outcomes = [1]
            with patch('compose_quickstart.subprocess.call', side_effect=compose), patch.object(sys, 'argv', argv):
                self.assertEqual(1, compose_quickstart.main())
            self.assertEqual(1, len(calls), 'A failed start must not recreate the edge')
            self.assertTrue(marker.exists(), 'The pending restart must survive the failed start')

            calls.clear()
            outcomes = [0, 0]
            with patch('compose_quickstart.subprocess.call', side_effect=compose), patch.object(sys, 'argv', argv):
                self.assertEqual(0, compose_quickstart.main())
            self.assertEqual(['docker', 'compose', 'up', '-d', '--wait', '--wait-timeout', '180',
                              '--force-recreate', '--no-deps', 'console'], calls[1])
            self.assertFalse(marker.exists())

            calls.clear()
            outcomes = [0]
            with patch('compose_quickstart.subprocess.call', side_effect=compose), patch.object(sys, 'argv', argv):
                self.assertEqual(0, compose_quickstart.main())
            self.assertEqual(1, len(calls), 'Nothing changed, so the edge is left running')

    def test_console_requires_openssl_3(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare_console(Path(directory))
            env_file = path / '.env'
            # Leave initialize something to write, so the check must run before it.
            env_file.write_text(''.join(line + '\n' for line in env_file.read_text().splitlines()
                                        if not line.startswith('HONUA_CONSOLE_PORT=')))
            saved = env_file.read_bytes()
            real = subprocess.run
            libressl = subprocess.CompletedProcess(['openssl', 'version'], 0, 'LibreSSL 3.3.6\n', '')

            def run(command, *args, **kwargs):
                return libressl if command == ['openssl', 'version'] else real(command, *args, **kwargs)

            with patch('compose_quickstart.subprocess.run', side_effect=run):
                with self.assertRaisesRegex(RuntimeError, 'OpenSSL 3'):
                    initialize(path)
            self.assertEqual(saved, env_file.read_bytes())

    def test_changed_console_settings_restart_the_edge(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare_console(Path(directory))
            env_file = path / '.env'
            env_file.write_text(env_file.read_text() + 'HONUA_CONSOLE_V4=false\n')
            initialize(path)

            self.assertFalse(json.loads((path / 'console.config.json').read_text())['v4Enabled'])
            self.assertTrue((path / CONSOLE_RELOAD_MARKER).exists())

    def test_disabling_console_drops_a_pending_edge_restart(self):
        with tempfile.TemporaryDirectory() as directory:
            path = self.prepare_console(Path(directory))
            marker = path / CONSOLE_RELOAD_MARKER
            marker.touch()
            env_file = path / '.env'
            env_file.write_text(env_file.read_text().replace('COMPOSE_PROFILES=console', 'COMPOSE_PROFILES='))
            calls = []
            argv = ['quickstart.py', '--directory', str(path)]

            with patch('compose_quickstart.subprocess.call', side_effect=lambda command, **_: calls.append(command) or 0), \
                    patch.object(sys, 'argv', argv):
                self.assertEqual(0, compose_quickstart.main())

            self.assertEqual(1, len(calls), 'A disabled Console is never recreated')
            self.assertFalse(marker.exists())


if __name__ == '__main__':
    unittest.main()
