#!/usr/bin/env python3
"""Prove producer partition coverage and reject overlapping project graphs."""
import importlib.util
import json
from pathlib import Path
import tempfile
import sys
import unittest

MODULE = Path(__file__).resolve().parents[1] / 'partition-managed-producer.py'
spec = importlib.util.spec_from_file_location('partition', MODULE)
partition = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = partition
spec.loader.exec_module(partition)
SERVER = partition.SERVER
OTHER = 'src/Other/Other.csproj'


class ProducerTests(unittest.TestCase):
    def test_partition_is_disjoint_and_complete(self):
        selected = [SERVER, OTHER, SERVER]
        server = set(partition.server_projects(selected))
        main = set(partition.main_projects(selected))
        self.assertFalse(server & main)
        self.assertEqual(set(selected), server | main)
        self.assertEqual([], partition.server_projects([OTHER]))
        for invalid in [None, {}, [1]]:
            with self.assertRaises(ValueError):
                partition.main_projects(invalid)

    def make_root(self, root, reference=''):
        for project in [SERVER, OTHER]:
            path = root / project
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('<Project>' + (reference if project == OTHER else '') + '</Project>')
        (root / 'Honua.sln').write_text('\n'.join(
            f'Project("x") = "project", "{project}", "id"' for project in [SERVER, OTHER]))

    def test_full_and_scoped_builds_retain_every_other_project(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_root(root)
            source, output = root / 'source.slnf', root / 'output.slnf'
            source.write_text(json.dumps({'solution': {'projects': [SERVER, OTHER]}}))
            for mode in ['full', 'affected', 'test-roots']:
                partition.exclude_server(partition.BuildFilterRequest(root, mode, source, output))
                self.assertEqual([OTHER], json.loads(output.read_text())['solution']['projects'])
            for projects in [[SERVER], ['unknown.csproj']]:
                source.write_text(json.dumps({'solution': {'projects': projects}}))
                with self.assertRaises(ValueError):
                    partition.exclude_server(partition.BuildFilterRequest(root, 'affected', source, output))

    def test_reverse_dependency_rejects_duplicate_compile(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_root(root, f'<ItemGroup><ProjectReference Include="../../{SERVER}" /></ItemGroup>')
            with self.assertRaises(ValueError):
                partition.exclude_server(partition.BuildFilterRequest(root, 'full', None, root / 'output.slnf'))


if __name__ == '__main__':
    unittest.main()
