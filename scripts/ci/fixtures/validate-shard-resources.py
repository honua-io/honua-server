#!/usr/bin/env python3
"""Resource-free skips preserve matrix ownership and conservative defaults."""
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('resources', ROOT / 'scripts/ci/annotate-shard-resources.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ResourceTests(unittest.TestCase):
    def test_reviewed_resource_free_only(self):
        matrix = [{'csproj': name, 'shard_name': name} for name in
                  ['unit', 'component', 'database', 'mixed', 'missing', 'malformed', '']]
        catalog = {'projects': {
            'unit': {'role': 'unit', 'resources': []},
            'component': {'role': 'component', 'resources': []},
            'database': {'role': 'component', 'resources': ['postgres']},
            'mixed': {'role': 'mixed', 'resources': []},
            'malformed': {'role': 'unit', 'resources': None}}}
        result = module.annotate(matrix, catalog)
        self.assertEqual([False, False, True, True, True, True, True],
                         [row['needs_postgres'] for row in result])
        self.assertEqual(matrix, [{k: v for k, v in row.items() if k != 'needs_postgres'} for row in result])

    def test_unknown_catalog_retains_all_services(self):
        for catalog in ({}, None, {'projects': []}):
            self.assertTrue(module.annotate([{'csproj': 'unit'}], catalog)[0]['needs_postgres'])

    def test_real_full_matrix_retains_database_components_and_owners(self):
        catalog = json.loads((ROOT / '.github/test-projects.json').read_text())
        shards = json.loads((ROOT / '.github/ci-shards.json').read_text())['shards']
        result = module.annotate(shards, catalog)
        self.assertEqual(len(shards), len(result))
        self.assertEqual([s['name'] for s in shards], [s['name'] for s in result])
        for row in result:
            entry = catalog['projects'][row.get('csproj') or module.MONOLITH]
            if 'postgres' in entry['resources'] or entry['role'] not in {'unit', 'component'}:
                self.assertTrue(row['needs_postgres'], row['name'])
        self.assertTrue(any(not row['needs_postgres'] for row in result))

    def test_workflow_uses_conservative_conditional_image(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text().split('  server-tests:', 1)[1]
        self.assertIn("image: ${{ toJSON(matrix.needs_postgres) != 'false' && 'mirror.gcr.io/postgis/", workflow)
        self.assertIn("|| '' }}", workflow)
        self.assertIn("HONUA_TEST_DB_URL: ${{ toJSON(matrix.needs_postgres) != 'false'", workflow)


if __name__ == '__main__':
    unittest.main()
