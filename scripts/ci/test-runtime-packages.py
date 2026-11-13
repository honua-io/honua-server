import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class RuntimePackageMetadataTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.runtime = self.root / 'runtime'
        status = self.runtime / 'var/lib/dpkg/status'
        status.parent.mkdir(parents=True)
        status.write_text('Package: base-runtime\nStatus: install ok installed\nVersion: 1\n\n')
        self.additions = self.root / 'additions'
        self.packages = self.root / 'packages'
        self.packages.write_text('fixture-runtime\nfixture-runtime\n')
        tools = self.root / 'tools'
        tools.mkdir()
        query = tools / 'dpkg-query'
        query.write_text('''#!/bin/sh
case "$1" in
    --show) printf '%s' "$FIXTURE_PACKAGE_VERSION" ;;
    --status) printf 'Package: fixture-runtime\nStatus: install ok installed\nVersion: %s\n\n' "$FIXTURE_PACKAGE_VERSION" ;;
    *) exit 1 ;;
esac
''')
        query.chmod(0o755)
        cache = tools / 'apt-cache'
        cache.write_text('#!/bin/sh\nprintf "  Candidate: %s\\n" "$FIXTURE_CANDIDATE_VERSION"\n')
        cache.chmod(0o755)
        self.environment = {**os.environ, 'PATH': str(tools) + os.pathsep + os.environ['PATH'],
                            'FIXTURE_PACKAGE_VERSION': '2', 'FIXTURE_CANDIDATE_VERSION': '2'}

    def record(self):
        script = Path(__file__).resolve().parents[2] / 'docker/runtime/write-package-status.sh'
        return subprocess.run(['sh', str(script), str(self.runtime), str(self.additions), str(self.packages)],
                              env=self.environment, text=True, capture_output=True, timeout=10)

    def test_native_package_is_recorded_once_and_base_manifest_is_preserved(self):
        result = self.record()
        self.assertEqual(result.returncode, 0, result.stderr)
        status = (self.additions / 'var/lib/dpkg/status').read_text()
        self.assertIn('Package: base-runtime\n', status)
        self.assertEqual(status.count('Package: fixture-runtime\n'), 1)
        self.assertIn('Version: 2\n', status)

    def test_copied_package_older_than_security_candidate_is_rejected(self):
        self.environment['FIXTURE_PACKAGE_VERSION'] = '1'
        result = self.record()
        self.assertNotEqual(result.returncode, 0)

    def test_copied_package_replaces_its_old_status_record(self):
        status = self.runtime / 'var/lib/dpkg/status'
        with status.open('a') as stream:
            stream.write('Package: fixture-runtime\nStatus: install ok installed\nVersion: 1\n\n')
        result = self.record()
        self.assertEqual(result.returncode, 0, result.stderr)
        merged = (self.additions / 'var/lib/dpkg/status').read_text()
        self.assertEqual(merged.count('Package: fixture-runtime\n'), 1)
        self.assertNotIn('Version: 1\n', merged.split('Package: fixture-runtime\n')[1])


if __name__ == '__main__':
    unittest.main()
