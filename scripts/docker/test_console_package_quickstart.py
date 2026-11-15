"""Console in the customer package: the released bundle is verified before anything is written."""
import hashlib
import json
from pathlib import Path
import stat
import tempfile
import unittest
import zipfile

from package_quickstart import CONSOLE_WEB_IMAGE
from quickstart_testing import QuickstartFixture


class ConsolePackageTests(QuickstartFixture, unittest.TestCase):
    def test_package_carries_the_verified_console_bundle(self):
        with tempfile.TemporaryDirectory() as directory:
            path, _, digest = self.packaged_console_install(Path(directory))
            self.assertTrue((path / 'console/_framework/dotnet.js').is_file())
            lock = json.loads((path / 'images.json').read_text())
            self.assertEqual(CONSOLE_WEB_IMAGE, lock['consoleWeb'])
            self.assertEqual(dict(sha256=digest, files=3), lock['consoleBundle'])
            self.assertNotIn('console', lock)
            self.assertIn('--profile console', (path / 'README.md').read_text())

    def test_console_bundle_must_match_its_release_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle, digest = self.console_bundle(Path(directory))
            output = Path(directory) / 'install'
            for expected in ('0' * 64, digest.upper(), '', None):
                with self.subTest(expected=expected):
                    with self.assertRaisesRegex(ValueError, 'sha256'):
                        self.package_console(output, (bundle, expected))
                    self.assertFalse(output.exists())

    def test_unsafe_or_foreign_console_bundles_are_rejected_before_writing_package(self):
        cases = {
            'traversal': {'../escape.js': b'x'},
            'absolute': {'/etc/escape.js': b'x'},
            'missing entrypoint': {'index.html': None},
            'root base path': {'index.html': b'<base href="/" />'},
            'case collision': {'_framework/Dotnet.js': b'x'},
            'file and folder': {'_framework': b'x'},
        }
        for name, change in cases.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                bundle, digest = self.console_bundle(Path(directory), change)
                output = Path(directory) / 'install'
                with self.assertRaises(ValueError):
                    self.package_console(output, (bundle, digest))
                self.assertFalse(output.exists())

    def test_console_bundle_folder_entry_cannot_share_a_file_path(self):
        for folder, clash in (('assets/', 'Assets'), ('a/b/', 'A')):
            with self.subTest(folder=folder), tempfile.TemporaryDirectory() as directory:
                bundle = Path(directory) / 'console.zip'
                with zipfile.ZipFile(bundle, 'w') as archive:
                    for entry, content in self.console_bundle_files().items():
                        archive.writestr(entry, content)
                    archive.writestr(zipfile.ZipInfo(folder), b'')
                    archive.writestr(clash, b'x')
                digest = hashlib.sha256(bundle.read_bytes()).hexdigest()
                output = Path(directory) / 'install'
                with self.assertRaisesRegex(ValueError, 'both a file and a folder'):
                    self.package_console(output, (bundle, digest))
                self.assertFalse(output.exists())

    def test_console_bundle_symlinks_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / 'console.zip'
            with zipfile.ZipFile(bundle, 'w') as archive:
                for entry, content in self.console_bundle_files().items():
                    archive.writestr(entry, content)
                link = zipfile.ZipInfo('_framework/link.js')
                link.external_attr = (stat.S_IFLNK | 0o777) << 16
                archive.writestr(link, '/etc/passwd')
            digest = hashlib.sha256(bundle.read_bytes()).hexdigest()
            with self.assertRaisesRegex(ValueError, 'unsafe path'):
                self.package_console(Path(directory) / 'install', (bundle, digest))

    def test_package_cli_writes_nothing_for_a_bundle_that_fails_its_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle, _ = self.console_bundle(Path(directory))
            output = Path(directory) / 'install'

            result = self.run_package_cli(output, bundle, '0' * 64)

            self.assertNotEqual(0, result.returncode)
            self.assertIn('does not match its release sha256', result.stderr)
            self.assertFalse(output.exists())
            self.assertFalse(output.with_suffix('.tar.gz').exists())


if __name__ == '__main__':
    unittest.main()
