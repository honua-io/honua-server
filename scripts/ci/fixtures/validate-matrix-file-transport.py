#!/usr/bin/env python3
"""Exercise oversized shard selections without passing filters through exec."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]
CI = yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text())['jobs']
SAFE_ENV_BYTES = 64 * 1024


def step(job, name):
    return next(item for item in CI[job]['steps'] if item.get('name') == name)


class MatrixTransportTests(unittest.TestCase):
    def test_large_matrix_reaches_producer_cache_and_enforced_verdict(self):
        project = 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'
        matrix = [{'shard_name': 'fixture', 'csproj': project,
                   'filter': 'FullyQualifiedName~' + 'x' * (205 * 1024)}]
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            (folder / 'managed-matrix.json').write_text(json.dumps(matrix))
            env = dict(os.environ, RUNNER_TEMP=directory, AFFECTED_MODE='full',
                       GITHUB_OUTPUT=str(folder / 'output'))
            plan = step('build', 'Select managed producer outputs and build graph')
            self.assertNotIn('SELECTED_MATRIX', plan.get('env', {}))
            subprocess.run(['bash', '-e', '-c', plan['run']], cwd=ROOT, env=env,
                           check=True, capture_output=True)
            self.assertIn(project, (folder / 'managed-projects.json').read_text())
            cache = subprocess.run([
                'bash', 'scripts/ci/server-test-shard-cache.sh', 'plan',
                '--shard', 'fixture', '--project', project, '--matrix-file',
                str(folder / 'managed-matrix.json'), '--source-sha', 'a' * 40,
                '--runner-os', 'Linux', '--sdk', '10.0.403', '--run-id', '1'],
                cwd=ROOT, env=env, capture_output=True, text=True)
            self.assertEqual(0, cache.returncode, cache.stderr)
            verdict = subprocess.run([
                'python3', 'scripts/ci/affected-shard-verdict.py', '--matrix-file',
                str(folder / 'managed-matrix.json'), '--timings', str(folder),
                '--receipt', str(folder / 'receipt.json')], cwd=ROOT,
                env=dict(env, MODE='enforce', SELECT_RESULT='success', SELECT_SKIP='false',
                         SELECT_COUNT='1', SHARDS_RESULT='success'), capture_output=True)
            self.assertNotEqual(0, verdict.returncode, 'Missing execution evidence must fail')
            self.assertEqual('no_receipts', json.loads((folder / 'receipt.json').read_text())['verdict'])

    def test_matrix_artifact_is_required_by_every_full_ci_consumer(self):
        upload = step('targeted-shards', 'Upload selected shard matrix')
        self.assertEqual('error', upload['with']['if-no-files-found'])
        for job in ['build', 'server-tests', 'test-all']:
            download = step(job, 'Download selected shard matrix')
            self.assertIn('needs.targeted-shards.outputs.matrix_artifact', download['with']['name'])
            self.assertFalse(download.get('continue-on-error', False))
        self.assertIn('github.run_attempt', upload['with']['name'])

    def test_large_matrix_cannot_enter_workflow_environment(self):
        for filename in ['ci.yml', 'pr-gate.yml']:
            jobs = yaml.safe_load((ROOT / '.github/workflows' / filename).read_text())['jobs']
            for job in jobs.values():
                environments = [job.get('env', {})]
                environments.extend(item.get('env', {}) for item in job.get('steps', []))
                for environment in environments:
                    for value in environment.values():
                        self.assertNotIn('outputs.matrix_include', str(value))
        config = json.loads((ROOT / '.github/ci-shards.json').read_text())
        receipts = [{'shard_name': entry['shard_name']} for entry in config['shards']]
        self.assertLess(len(json.dumps(receipts).encode()), SAFE_ENV_BYTES)


if __name__ == '__main__':
    unittest.main()
