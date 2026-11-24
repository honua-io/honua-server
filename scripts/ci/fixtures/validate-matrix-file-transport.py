#!/usr/bin/env python3
"""Exercise oversized shard selections without passing filters through exec."""
import json
import os
from pathlib import Path
import re
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
    def test_full_selection_fits_job_outputs_without_losing_filters(self):
        compute = step('targeted-shards', 'Compute shard descriptor and matrix include')['run']
        # Empty expressions select the ordinary manual-full path.
        compute = re.sub(r'\$\{\{.*?\}\}', '', compute)
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            output = folder / 'output'
            subprocess.run(['bash', '-e', '-c', compute], cwd=ROOT,
                           env=dict(os.environ, RUNNER_TEMP=directory,
                                    GITHUB_OUTPUT=str(output), GITHUB_RUN_ID='1',
                                    GITHUB_RUN_ATTEMPT='1'), check=True, capture_output=True)
            self.assertLess(len(output.read_text().encode('utf-16-le')), 1024 * 1024)
            values = dict(line.split('=', 1) for line in output.read_text().splitlines())
            scheduled = json.loads(values['matrix_include'])
            artifact = json.loads((folder / 'managed-matrix.json').read_text())
            source = json.loads((ROOT / '.github/ci-shards.json').read_text())['shards']
            self.assertEqual({s['shard_name']: s['filter'] for s in source if not s.get('advisory')},
                             {s['shard_name']: s['filter'] for s in artifact})
            self.assertEqual([{k: v for k, v in s.items() if k != 'filter'}
                              for s in artifact], scheduled)

    def test_preview_projects_are_advisory_and_only_preview_selection_uses_smoke(self):
        catalog = json.loads((ROOT / '.github/ci-shards.json').read_text())
        preview = []
        for shard in catalog['shards']:
            projects = shard.get('csprojs') or [shard.get('csproj', '')]
            if any('.SensorThings.' in project for project in projects):
                self.assertTrue(shard.get('advisory'), shard['name'])
                self.assertTrue(all('.SensorThings.' in project for project in projects))
                preview.append(shard['name'])
        self.assertIn('SensorThings 5', preview)
        required = 'Protocol Units'
        for names, expected, reason in [
                (preview, catalog['default_shards_when_no_match'], 'no_path_match'),
                (preview + [required], [required], 'paths'),
                ([], [], 'paths')]:
            result = subprocess.check_output([
                'jq', '--argjson', 'selection', json.dumps(dict(shards=names, reason='paths')),
                '-f', 'scripts/ci/required-shard-descriptor.jq', '.github/ci-shards.json'],
                cwd=ROOT)
            descriptor = json.loads(result)
            self.assertEqual(expected, descriptor['shards'])
            self.assertEqual(reason, descriptor['reason'])

    def test_shard_loads_exact_filter_and_rejects_missing_or_ambiguous_selection(self):
        run = step('server-tests', 'Run server test shard')['run']
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            scripts = folder / 'scripts/ci'
            scripts.mkdir(parents=True)
            stub = scripts / 'run-server-test-shard.sh'
            stub.write_text('#!/bin/sh\nprintf "%s" "$HONUA_SERVER_TEST_FILTER"\n')
            stub.chmod(0o755)
            selected = {'shard_name': 'fixture', 'filter': '(Name~one|Name~two)&Tier!=Slow'}
            for matrix, valid in [([selected], True), ([], False),
                                  ([selected, selected], False),
                                  ([dict(selected, filter='')], False)]:
                (folder / 'managed-matrix.json').write_text(json.dumps(matrix))
                result = subprocess.run(['bash', '-e', '-c', run], cwd=folder,
                                        env=dict(os.environ, RUNNER_TEMP=directory,
                                                 HONUA_SERVER_TEST_SHARD_NAME='fixture'),
                                        capture_output=True, text=True)
                self.assertEqual(valid, result.returncode == 0, result.stderr)
                if valid:
                    self.assertEqual(selected['filter'], result.stdout)
                else:
                    self.assertEqual('', result.stdout)

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
