"""The private prior changes only the embedded application migration boundary."""
from pathlib import Path
import tempfile
import unittest
from prepare_lifecycle_prior import cap_schema


class LifecyclePriorTests(unittest.TestCase):
    def test_preserves_old_scripts_and_other_content(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            migrations = root / 'src/Honua.Server/Migrations'
            migrations.mkdir(parents=True)
            for name in ('001_Init.sql', '140_Expand.sql', '141_Contract.sql',
                         '142_Financial.sql', 'notes.txt'):
                (migrations / name).write_text(name)
            entrypoint = root / 'secret-entrypoint.c'
            entrypoint.write_text('launcher source')
            cap_schema(root)
            self.assertEqual(['001_Init.sql', '140_Expand.sql', 'notes.txt'],
                             sorted(path.name for path in migrations.iterdir()))
            self.assertEqual('140_Expand.sql', (migrations / '140_Expand.sql').read_text())
            self.assertEqual('launcher source', entrypoint.read_text())

    def test_rejects_wrong_source_before_modifying_files(self):
        with tempfile.TemporaryDirectory() as directory:
            migrations = Path(directory) / 'src/Honua.Server/Migrations'
            migrations.mkdir(parents=True)
            script = migrations / '142_Financial.sql'
            script.write_text('unchanged')
            with self.assertRaisesRegex(ValueError, 'schema 140'):
                cap_schema(directory)
            self.assertEqual('unchanged', script.read_text())


if __name__ == '__main__':
    unittest.main()
