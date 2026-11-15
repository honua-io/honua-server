#!/usr/bin/env python3
"""Real Git ownership tests; only exact canonical publication proof grants a bypass."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('ownership', Path(__file__).with_name('check-generated-file-ownership.py'))
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


class OwnershipTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.previous = Path.cwd()
        os.chdir(self.temp.name)
        self.addCleanup(os.chdir, self.previous)
        self.git('init', '-q')
        self.git('config', 'user.name', 'Test')
        self.git('config', 'user.email', 'test@example.invalid')
        self.write('scripts/ci/generated-files.sh', '#!/bin/bash\nGENERATED_FILES=(\n  docs/generated\n)\n')
        self.write('docs/generated/page.md', 'old\n')
        self.write('source.txt', 'old\n')
        self.commit()
        self.base = self.git('rev-parse', 'HEAD')

    def git(self, *args):
        return subprocess.check_output(['git', *args], text=True, stderr=subprocess.DEVNULL).strip()

    def write(self, path, content):
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)

    def commit(self):
        self.git('add', '.')
        self.git('commit', '-qm', 'fixture')
        return self.git('rev-parse', 'HEAD')

    def test_authored_change_needs_no_proof(self):
        self.write('source.txt', 'new\n')
        head = self.commit()
        with patch.object(policy.proof, 'producer_proof', side_effect=AssertionError('unexpected API call')):
            self.assertEqual(policy.check(self.base, head), [])

    def test_generated_change_without_proof_is_rejected(self):
        self.write('docs/generated/page.md', 'new\n')
        head = self.commit()
        with patch.object(policy.proof, 'producer_proof', return_value=False):
            self.assertEqual(policy.check(self.base, head), ['docs/generated/page.md'])

    def test_exact_proven_publication_passes_even_after_trunk_advances(self):
        self.write('docs/generated/page.md', 'new\n')
        head = self.commit()
        self.git('checkout', '-qb', 'trunk-advanced', self.base)
        self.write('source.txt', 'trunk change\n')
        trunk = self.commit()
        with patch.object(policy.proof, 'producer_proof', return_value=True) as proof:
            self.assertEqual(policy.check(trunk, head), [])
            proof.assert_called_once_with(self.base, head, self.git('rev-parse', f'{head}^{{tree}}'))

    def test_mixed_source_change_cannot_use_proof(self):
        self.write('docs/generated/page.md', 'new\n')
        self.write('source.txt', 'new\n')
        head = self.commit()
        with patch.object(policy.proof, 'producer_proof', side_effect=AssertionError('mixed diff cannot bypass')):
            self.assertEqual(policy.check(self.base, head), ['docs/generated/page.md'])

    def test_deletion_and_rename_cannot_evade_ownership(self):
        self.git('mv', 'docs/generated/page.md', 'docs/authored.md')
        head = self.commit()
        self.assertEqual(policy.check(self.base, head), ['docs/generated/page.md'])

    def test_head_cannot_remove_its_own_policy(self):
        self.write('scripts/ci/generated-files.sh', 'GENERATED_FILES=(\n  other/path\n)\n')
        self.write('docs/generated/page.md', 'new\n')
        head = self.commit()
        self.assertEqual(policy.check(self.base, head), ['docs/generated/page.md'])


if __name__ == '__main__':
    unittest.main()
