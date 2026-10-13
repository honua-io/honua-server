#!/usr/bin/env python3
"""Resource-free skips preserve matrix ownership and conservative defaults."""
import importlib.util
import json
import argparse
import contextlib
import io
import os
import tempfile
from unittest.mock import patch
import subprocess
import sys
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

    def test_real_full_matrix_uses_stdin_beyond_argument_limit(self):
        shards = json.loads((ROOT / '.github/ci-shards.json').read_text())['shards']
        payload = json.dumps(shards)
        self.assertGreater(len(payload), 128 * 1024)
        result = json.loads(subprocess.check_output(
            [sys.executable, str(ROOT / 'scripts/ci/annotate-shard-resources.py'),
             '--catalog', str(ROOT / '.github/test-projects.json')], input=payload, text=True))
        self.assertEqual(shards, [{k: v for k, v in row.items() if k != 'needs_postgres'} for row in result])

    def test_both_workflows_use_the_same_conservative_resource_policy(self):
        for filename, job in [('ci.yml', 'server-tests'), ('pr-gate.yml', 'affected-shard')]:
            text = (ROOT / '.github/workflows' / filename).read_text()
            workflow = text.split('  ' + job + ':', 1)[1]
            self.assertIn("image: ${{ toJSON(matrix.needs_postgres) != 'false' && 'mirror.gcr.io/postgis/", workflow)
            self.assertIn("|| '' }}", workflow)
            self.assertIn("HONUA_TEST_DB_URL: ${{ toJSON(matrix.needs_postgres) != 'false'", workflow)
            if filename == 'ci.yml':
                self.assertIn('"${matrix_include}" | python3 scripts/ci/annotate-shard-resources.py', text)
            else:
                self.assertIn('--annotate-resources \\\n            --github-output', text)

    def test_pr_matrix_annotation_preserves_selection_receipt_filters_and_budgets(self):
        spec = importlib.util.spec_from_file_location('selection', ROOT / 'scripts/ci/compute-affected-shards.py')
        selection = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(selection)
        registry = json.loads((ROOT / '.github/ci-shards.json').read_text())
        catalog = json.loads((ROOT / '.github/test-projects.json').read_text())['projects']
        free = next(s for s in registry['shards'] if catalog[s.get('csproj') or module.MONOLITH]['role'] == 'unit'
                    and catalog[s.get('csproj') or module.MONOLITH]['resources'] == [])
        database = next(s for s in registry['shards'] if 'postgres' in catalog[s.get('csproj') or module.MONOLITH]['resources'])
        result = {'skip': False, 'reason': 'targeted', 'shards': [free['name'], database['name']], 'cap': 6}
        expected = selection.matrix_include(result, registry)
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'github-output'
            receipt = Path(directory) / 'selection.json'
            args = argparse.Namespace(annotate_resources=True, json_path=str(receipt), summary_path=None, github_output=True)
            with patch.object(selection, '_repo_root', return_value=ROOT), patch.object(selection, 'render_summary', return_value='fixture'), \
                    patch.dict(os.environ, {'GITHUB_OUTPUT': str(output)}), contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(0, selection.emit(result, registry, args))
            actual = json.loads(receipt.read_text())
            self.assertEqual(result, {k: v for k, v in actual.items() if k != 'executionMatrix'})
            matrix = actual['executionMatrix']
            self.assertEqual(expected, [{k: v for k, v in row.items() if k != 'needs_postgres'} for row in matrix])
            flags = {row['csproj']: row['needs_postgres'] for row in matrix}
            self.assertFalse(flags[free['csproj']])
            self.assertTrue(flags[database.get('csproj') or module.MONOLITH])
            emitted = next(line.split('=', 1)[1] for line in output.read_text().splitlines() if line.startswith('matrix_include='))
            self.assertEqual(matrix, json.loads(emitted))


if __name__ == '__main__':
    unittest.main()
