#!/usr/bin/env python3
"""Real Git rename selection must preserve both source owners."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]


def load(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'scripts/ci' / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class RenameSelectionTests(unittest.TestCase):
    def test_real_rename_selects_old_and_new_owners(self):
        local = load('local-test-feedback')
        hosted = load('compute-affected-shards')
        old = 'src/Honua.Protocols.Stac/RenameFixture.cs'
        new = 'src/Honua.Protocols.Scene/RenameFixture.cs'
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def git(*args):
                return subprocess.check_output(['git', '-C', str(root), *args], text=True).strip()
            git('init', '-q')
            git('config', 'user.name', 'Fixture')
            git('config', 'user.email', 'fixture@example.invalid')
            (root / old).parent.mkdir(parents=True)
            (root / old).write_text('// Exact bytes deliberately trigger Git rename detection.\n')
            git('add', '.')
            git('commit', '-qm', 'baseline')
            base = git('rev-parse', 'HEAD')
            (root / new).parent.mkdir(parents=True)
            git('mv', old, new)
            self.assertTrue(git('diff', '--cached', '--name-status').startswith('R100'))
            local_paths = local.changed_files(root, base)
            self.assertEqual({old, new}, set(local_paths))
            git('commit', '-qm', 'rename')
            hosted_paths = hosted.git_changed_files(root, base, 'HEAD')
            self.assertEqual({old, new}, set(hosted_paths))
            # Use the canonical real registry, not a duplicate ownership model.
            descriptor = json.loads(subprocess.check_output(
                ['bash', str(ROOT / 'scripts/ci/honua-server-targeted-tests.sh'), '--stdin'],
                input='\n'.join(hosted_paths), text=True))
            self.assertFalse(descriptor['run_all'])
            self.assertIn('Protocols Stac Units', descriptor['shards'])
            self.assertIn('Protocols Scene Units', descriptor['shards'])
            self.assertIsNone(hosted.git_changed_files(root, 'absent-base', 'HEAD'))


if __name__ == '__main__':
    unittest.main()
