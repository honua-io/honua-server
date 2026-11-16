"""Shared fixtures for the customer package tests (not a test module)."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import zipfile

from compose_quickstart import ALL_SECRET_KEYS, initialize
from package_quickstart import add_console_bundle, package, read_console_bundle

ROOT = Path(__file__).resolve().parents[2]
SERVER = 'ghcr.io/honua-io/honua-server@sha256:' + '1' * 64
EXECUTOR = 'ghcr.io/honua-io/honua-server@sha256:' + '2' * 64
WORKER = 'ghcr.io/honua-io/honua-worker@sha256:' + '3' * 64
PACKAGE_IMAGES = dict(server=SERVER, executor=EXECUTOR, worker=WORKER)


class QuickstartFixture:
    """Mixed into unittest.TestCase classes."""

    def prepare(self, directory):
        path = Path(directory)
        package(path, PACKAGE_IMAGES, '2026.1-rc.2')
        initialize(path)
        return path

    def compose_env(self, path):
        values = dict(line.split('=', 1) for line in (path / '.env').read_text().splitlines())
        # The disposable project must win over ambient Compose flags, including cleanup.
        excluded = (*ALL_SECRET_KEYS, 'LayerPublishing__MaterializationTimeoutSeconds')
        return dict({k: v for k, v in os.environ.items() if k not in excluded}, **values)

    def prepare_console(self, directory):
        path = directory / 'install'
        self.package_console(path, self.console_bundle(directory))
        initialize(path, ('console',))
        return path

    def package_console(self, destination, console):
        """Checks the bundle first, as package_quickstart.py does, then writes the package."""
        checked = read_console_bundle(*console)
        package(destination, PACKAGE_IMAGES, 'test')
        add_console_bundle(destination, checked)

    @staticmethod
    def console_bundle_files():
        return {
            'index.html': b'<!DOCTYPE html><html><head><base href="/console/" /></head><body></body></html>',
            'config.json': json.dumps(dict(apiBaseUrl='/', auth=dict(mode='builtin'), v4Enabled=True)).encode(),
            '_framework/dotnet.js': b'export {};',
        }

    def console_bundle(self, directory, change=None):
        """A minimal released Console archive and its receipt sha256."""
        files = dict(self.console_bundle_files(), **(change or {}))
        bundle = directory / 'honua-console-test.zip'
        with zipfile.ZipFile(bundle, 'w') as archive:
            for entry, content in files.items():
                if content is not None:
                    archive.writestr(entry, content)
        return bundle, hashlib.sha256(bundle.read_bytes()).hexdigest()

    def run_package_cli(self, output, bundle, digest):
        return subprocess.run([
            sys.executable, str(ROOT / 'scripts/docker/package_quickstart.py'),
            '--server-image', SERVER, '--executor-image', EXECUTOR, '--worker-image', WORKER,
            '--console-bundle', str(bundle), '--console-bundle-sha256', digest,
            '--version', 'test', '--output', str(output)], capture_output=True, text=True)

    def packaged_console_install(self, directory):
        """Packages through the CLI, enables Console with a customer override and renders Compose."""
        path = directory / 'install'
        bundle, digest = self.console_bundle(directory)
        packaged = self.run_package_cli(path, bundle, digest)
        self.assertEqual(0, packaged.returncode, packaged.stderr)
        (path / '.env').write_text('COMPOSE_FILE=compose.yaml:compose.executor.yaml:customer.yaml:compose.executor.image.yaml\n')
        (path / 'customer.yaml').write_text('services:\n  honua:\n    environment:\n      CUSTOMER_SETTING: preserved\n')
        initialized = subprocess.run([sys.executable, str(path / 'quickstart.py'), '--profile', 'console', '--init-only'],
                                     capture_output=True, text=True)
        self.assertEqual(0, initialized.returncode, initialized.stderr)
        rendered = subprocess.run(['docker', 'compose', 'config', '--format', 'json'],
                                  cwd=path, env=self.compose_env(path), capture_output=True, text=True)
        self.assertEqual(0, rendered.returncode, rendered.stderr)
        return path, json.loads(rendered.stdout), digest
