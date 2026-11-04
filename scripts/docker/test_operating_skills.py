"""Ensure the quickstart carries the canonical agent files with verifiable bytes."""
import hashlib
from pathlib import Path
import subprocess
import tempfile
import tarfile
import sys
import unittest

from package_quickstart import ROOT, package

IMAGE = 'ghcr.io/honua-io/honua-server@sha256:' + 'a' * 64


class OperatingSkillsTests(unittest.TestCase):
    def test_package_preserves_every_skill_file_and_pins_agent_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory)
            package(destination, IMAGE, IMAGE, IMAGE, 'test')
            source_files = sorted(path for path in (ROOT / 'skills').rglob('*') if path.is_file())
            expected = {'AGENTS.md'}
            for source in source_files:
                relative = source.relative_to(ROOT)
                self.assertEqual(source.read_bytes(), (destination / relative).read_bytes())
                expected.add(relative.as_posix())
            actual = {}
            for line in (destination / 'agent-files.sha256').read_text().splitlines():
                digest, name = line.split('  ', 1)
                self.assertNotIn(name, actual)
                actual[name] = digest
                self.assertEqual(hashlib.sha256((destination / name).read_bytes()).hexdigest(), digest)
            self.assertEqual(expected, set(actual))
            result = subprocess.run(['sha256sum', '-c', 'agent-files.sha256'], cwd=destination,
                                    capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            changed = destination / 'skills/honua-install/SKILL.md'
            changed.write_bytes(changed.read_bytes() + b'changed\n')
            result = subprocess.run(['sha256sum', '-c', 'agent-files.sha256'], cwd=destination,
                                    capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)

    def test_release_archive_preserves_skill_bytes_and_lock(self):
        with tempfile.TemporaryDirectory() as directory:
            destination = Path(directory) / 'quickstart'
            result = subprocess.run(
                [sys.executable, str(ROOT / 'scripts/docker/package_quickstart.py'),
                 '--server-image', IMAGE, '--executor-image', IMAGE,
                 '--worker-image', IMAGE, '--version', 'test', '--output', str(destination)],
                capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            with tarfile.open(destination.with_suffix('.tar.gz')) as archive:
                for path in (destination / 'skills').rglob('*'):
                    if path.is_file():
                        member = path.relative_to(destination).as_posix()
                        self.assertEqual(path.read_bytes(), archive.extractfile(member).read())
                self.assertEqual((destination / 'agent-files.sha256').read_bytes(),
                                 archive.extractfile('agent-files.sha256').read())


if __name__ == '__main__':
    unittest.main()
