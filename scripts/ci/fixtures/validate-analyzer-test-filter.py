#!/usr/bin/env python3
"""Verify analyzer root partitions preserve full and affected coverage."""
import importlib.util
import json
from pathlib import Path
import tempfile
import sys
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'analyzer-test-filter.py'
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

spec = importlib.util.spec_from_file_location('test_filter', SCRIPT)
filters = importlib.util.module_from_spec(spec)
spec.loader.exec_module(filters)


class PartitionTests(unittest.TestCase):
    def test_full_and_affected_partitions_preserve_exact_union(self):
        full = {filters.SERVER, 'src/Source/Source.csproj', 'tests/Other/Other.csproj'}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            windows_projects = {project.replace('/', '\\') for project in full}
            (root / 'Honua.sln').write_text('\n'.join(
                f'Project("type") = "Project", "{project}", "id"'
                for project in windows_projects))
            server = set(filters.partition(root, None, 'server'))
            other = set(filters.partition(root, None, 'other'))
            self.assertEqual(full, server | other)
            self.assertEqual({filters.SERVER}, server)
            self.assertFalse(server & other)
            path = Path(directory) / 'scope.slnf'
            for selected in [full, {filters.SERVER}, full - {filters.SERVER}, set()]:
                path.write_text(json.dumps({'solution': {'path': str(root / 'Honua.sln'),
                                                       'projects': [p.replace('/', '\\') for p in selected]}}))
                server = set(filters.partition(root, path, 'server'))
                other = set(filters.partition(root, path, 'other'))
                self.assertEqual(selected, server | other)
                self.assertFalse(server & other)
                self.assertLessEqual(server, {filters.SERVER})

    def test_invalid_filter_fails_closed(self):
        root = SCRIPT.parents[2]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'scope.slnf'
            for solution, projects in [('wrong.sln', []), (str(root / 'Honua.sln'), ['unknown.csproj']),
                                       (str(root / 'Honua.sln'), 'not-an-array'),
                                       (str(root / 'Honua.sln'), [42])]:
                path.write_text(json.dumps({'solution': {'path': solution, 'projects': projects}}))
                with self.assertRaises(ValueError):
                    filters.partition(root, path, 'server')


if __name__ == '__main__':
    unittest.main()
