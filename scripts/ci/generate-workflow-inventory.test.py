#!/usr/bin/env python3
"""Exercise inventory projection against independent workflow/template edits."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('inventory', Path(__file__).with_name('generate-workflow-inventory.py'))
inventory = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inventory)


class InventoryTests(unittest.TestCase):
    def test_projects_new_changed_and_removed_workflows_preserving_notes_and_history(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            workflows = root / '.github/workflows'
            workflows.mkdir(parents=True)
            ci = root / 'scripts/ci'
            ci.mkdir(parents=True)
            (ci / 'workflow-inventory.template.md').write_text(
                '# Inventory\n\nAuthored context.\n'
                '| `old.yml` | Old name | `push` | Preserve this note. |\n'
                '| `removed.yml` | Removed | `push` | Stale live entry. |\n'
                '## Recently retired\n| `retired.yml` | Yesterday | Historical reason. |\n')
            (workflows / 'old.yml').write_text('name: New name\non: [pull_request, workflow_dispatch]\n')
            (workflows / 'added.yaml').write_text('name: "Added | workflow"\non:\n  push:\n')
            output = inventory.render(root)
            self.assertIn('Authored context.', output)
            self.assertIn('| `old.yml` | New name | `pull_request`, `workflow_dispatch` | Preserve this note. |', output)
            self.assertIn('| `added.yaml` | Added &#124; workflow | `push` | |', output)
            self.assertNotIn('removed.yml', output)
            self.assertIn('| `retired.yml` | Yesterday | Historical reason. |', output)
            self.assertEqual(output, inventory.render(root))

    def test_duplicate_live_rows_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / '.github/workflows').mkdir(parents=True)
            (root / 'scripts/ci').mkdir(parents=True)
            (root / '.github/workflows/test.yml').write_text('on: push\n')
            (root / 'scripts/ci/workflow-inventory.template.md').write_text(
                '| `test.yml` | Test | `push` | Note. |\n' * 2)
            with self.assertRaisesRegex(ValueError, 'duplicate live workflow row'):
                inventory.render(root)


if __name__ == '__main__':
    unittest.main()
