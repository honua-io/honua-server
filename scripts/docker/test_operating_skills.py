"""Ensure the quickstart carries the canonical agent files with verifiable bytes."""
import hashlib
import importlib.util
import re
from pathlib import Path
import subprocess
import tempfile
import tarfile
import sys
import unittest

from package_quickstart import ROOT, package

LINK_CHECK_SPEC = importlib.util.spec_from_file_location(
    'doc_links', ROOT / 'scripts/ci/check-doc-links.py')
DOC_LINKS = importlib.util.module_from_spec(LINK_CHECK_SPEC)
LINK_CHECK_SPEC.loader.exec_module(DOC_LINKS)


def skill_link_errors(skill_root):
    _, relative_errors, _ = DOC_LINKS.check_relative_links(str(skill_root), str(ROOT), set())
    references = []
    for path in skill_root.rglob('*.md'):
        for url in DOC_LINKS.RUNTIME_DOCS_URL_RE.findall(path.read_text()):
            references.append({'url': url.rstrip(DOC_LINKS.URL_TRAILING_TRIM)})
    manifest = {'references': list({entry['url']: entry for entry in references}.values())}
    _, docs_errors, _, _ = DOC_LINKS.check_manifest(
        manifest, str(ROOT), str(ROOT / 'docs'), {})
    return relative_errors + docs_errors


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

    def test_documented_host_installs_preserve_all_packaged_skill_bytes(self):
        for document in ('docs/get-started/quickstart.md',
                         'docs/guides/connect/operating-skills.md',
                         'docs/guides/connect/working-with-ai-agents.md'):
            with self.subTest(document=document), tempfile.TemporaryDirectory() as directory:
                destination = Path(directory)
                package(destination, IMAGE, IMAGE, IMAGE, 'test')
                commands = re.search(
                    r'```bash\n(mkdir -p \.claude/skills.*?)\n```',
                    (ROOT / document).read_text(), re.S).group(1)
                result = subprocess.run(['bash', '-eu', '-c', commands], cwd=destination,
                                        capture_output=True, text=True)
                self.assertEqual(0, result.returncode, result.stderr)
                for host in ('.claude', '.agents'):
                    installed = destination / host / 'skills'
                    sources = {path.relative_to(destination / 'skills'): path.read_bytes()
                               for path in (destination / 'skills').rglob('*') if path.is_file()}
                    copies = {path.relative_to(installed): path.read_bytes()
                              for path in installed.rglob('*') if path.is_file()}
                    self.assertEqual(sources, copies)

    def test_skill_relative_and_docs_links_resolve(self):
        self.assertEqual([], skill_link_errors(ROOT / 'skills'))

    def test_skill_links_reject_missing_files_and_anchors(self):
        targets = ('references/missing.md', '#missing',
                   'https://honua.io/docs/concepts/domain/missing/',
                   'https://honua.io/docs/concepts/domain/layer/#missing')
        for target in targets:
            with self.subTest(target=target), tempfile.TemporaryDirectory() as directory:
                skill_root = Path(directory)
                (skill_root / 'SKILL.md').write_text(f'# Skill\n[Reference]({target})\n')
                self.assertTrue(skill_link_errors(skill_root))

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
