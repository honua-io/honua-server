#!/usr/bin/env python3
"""Real Git admission and canonical output validation fixtures."""
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
spec = importlib.util.spec_from_file_location(
    'submitted', Path(__file__).with_name('check-submitted-generated-files.py'))
submitted = importlib.util.module_from_spec(spec)
spec.loader.exec_module(submitted)


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
        for path in policy.VERIFICATION_INPUTS:
            if path != 'scripts/ci/generated-files.sh':
                self.write(path, 'trusted validator fixture\n')
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

    def source_regeneration(self):
        self.write('src/route.cs', 'new source\n')
        self.write('docs/generated/page.md', 'canonical\n')
        return self.commit()

    def test_source_regeneration_requires_no_producer_exemption(self):
        head = self.source_regeneration()
        with patch.object(policy.proof, 'producer_proof', side_effect=AssertionError('no exemption')):
            self.assertEqual(policy.check(self.base, head), [])
        self.assertEqual(submitted.mismatches(self.base, head), [])

    def test_candidate_cannot_remove_or_modify_verification(self):
        for path in policy.VERIFICATION_INPUTS:
            with self.subTest(path=path):
                self.git('reset', '--hard', self.base)
                self.source_regeneration()
                Path(path).unlink()
                head = self.commit()
                self.assertEqual(policy.check(self.base, head), ['docs/generated/page.md'])
                self.write(path, 'disabled\n')
                head = self.commit()
                self.assertEqual(policy.check(self.base, head), ['docs/generated/page.md'])

    def test_inherited_drift_is_advisory_but_supplied_bytes_are_strict(self):
        self.write('docs/generated/inherited.md', 'stale\n')
        base = self.commit()
        head = self.source_regeneration()
        self.write('docs/generated/inherited.md', 'fresh\n')
        self.assertEqual(submitted.mismatches(base, head), [])
        self.write('docs/generated/page.md', 'different\n')
        self.assertEqual(submitted.mismatches(base, head), ['docs/generated/page.md'])

    def test_deleted_output_must_stay_absent_after_regeneration(self):
        Path('docs/generated/page.md').unlink()
        head = self.commit()
        self.assertEqual(submitted.mismatches(self.base, head), [])
        self.write('docs/generated/page.md', 'restored by generator\n')
        self.assertEqual(submitted.mismatches(self.base, head), ['docs/generated/page.md'])

    def test_added_output_must_match_regeneration(self):
        self.write('docs/generated/new.md', 'submitted\n')
        head = self.commit()
        self.assertEqual(submitted.mismatches(self.base, head), [])
        Path('docs/generated/new.md').unlink()
        self.assertEqual(submitted.mismatches(self.base, head), ['docs/generated/new.md'])

    def test_modes_and_symlinks_cannot_match_regular_generated_bytes(self):
        head = self.source_regeneration()
        path = Path('docs/generated/page.md')
        path.chmod(0o755)
        self.assertEqual(submitted.mismatches(self.base, head), ['docs/generated/page.md'])
        path.unlink()
        path.symlink_to('../../source.txt')
        self.assertEqual(submitted.mismatches(self.base, head), ['docs/generated/page.md'])


if __name__ == '__main__':
    unittest.main()
