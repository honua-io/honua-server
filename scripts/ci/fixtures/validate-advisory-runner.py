#!/usr/bin/env python3
"""Read-only heavy runner selection and hosted fallback contracts."""
import json
import os
import socket
from pathlib import Path
import subprocess
import tempfile
import time
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / 'scripts/ci/select-advisory-runner.sh'


class AdvisoryRunnerTests(unittest.TestCase):
    def select(self, options=None):
        options = options or {}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            heartbeat = {'version': 1, 'instance': options.get('instance', 'i-123abc'), 'at': int(time.time()) - options.get('age', 0),
                         'heavy': {'version': 1, 'idle': options.get('idle', 1)}}
            (path / 'heartbeat').write_text(json.dumps(heartbeat))
            (path / 'aws').write_text('''#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$MOCK_DIR/calls"
[[ "$AWS_MAX_ATTEMPTS" == 1 && "$AWS_EC2_METADATA_DISABLED" == true ]] || exit 9
case "$*" in
  *describe-auto-scaling-groups*) [[ "$MOCK_ERROR" != host ]] || exit 1; echo i-123abc;;
  *get-parameter*) [[ "$MOCK_ERROR" != heartbeat ]] || exit 1; cat "$MOCK_DIR/heartbeat";;
  *) exit 8;;
esac
''')
            (path / 'aws').chmod(0o755)
            env = {**os.environ, 'PATH': f'{directory}:{os.environ["PATH"]}',
                   'MOCK_DIR': directory, 'MOCK_ERROR': options.get('error', ''), 'GITHUB_OUTPUT': str(path / 'output'),
                   'HONUA_HEAVY_RUNNER': options.get('label', '["self-hosted","honua-heavy"]'), 'HOSTED_RUNNER': 'ubuntu-24.04'}
            env.pop('AWS_ACCESS_KEY_ID', None)
            if options.get('credential', 'present') == 'present':
                env['AWS_ACCESS_KEY_ID'] = 'fixture'
            result = subprocess.run(['bash', str(SCRIPT)], env=env, capture_output=True, text=True, check=True)
            output = (path / 'output').read_text().removeprefix('runner=').strip()
            calls = (path / 'calls').read_text() if (path / 'calls').exists() else ''
            self.assertNotIn('set-desired-capacity', calls)
            self.assertNotIn('start-instances', calls)
            self.assertIn('Advisory compute:', result.stdout)
            return json.loads(output), calls

    def test_fresh_idle_pool(self):
        selected, calls = self.select()
        self.assertEqual(selected, ['self-hosted', 'honua-heavy'])
        self.assertEqual(len(calls.splitlines()), 2)

    def test_group_selector_preserves_pool_restriction(self):
        for selector in ({'group': 'honua-heavy', 'labels': ['honua-heavy']},
                         {'group': 'honua-heavy', 'labels': 'honua-heavy'}, {'group': 'honua-heavy'}):
            with self.subTest(selector=selector):
                selected, calls = self.select({'label': json.dumps(selector)})
                self.assertEqual(selected, selector)
                self.assertEqual(len(calls.splitlines()), 2)

    def test_busy_stale_mismatched_or_future_heartbeat_falls_back(self):
        for options in ({'idle': 0}, {'age': 100}, {'age': -100}, {'instance': 'i-old'}, {'idle': 9}):
            with self.subTest(options=options):
                self.assertEqual(self.select(options)[0], 'ubuntu-24.04')

    def test_api_failure_falls_back(self):
        for error in ('host', 'heartbeat'):
            with self.subTest(error=error):
                self.assertEqual(self.select({'error': error})[0], 'ubuntu-24.04')

    def test_absent_credential_or_malformed_labels_never_query_aws(self):
        for options in ({'credential': 'absent'}, {'label': ''}, {'label': '"honua-heavy"'},
                        {'label': '[]'}, {'label': '[1]'}, {'label': '{"group":""}'},
                        {'label': '{"group":"honua-heavy","labels":[]}'}, {'label': '{"labels":["honua-heavy"]}'}):
            with self.subTest(options=options):
                selected, calls = self.select(options)
                self.assertEqual(selected, 'ubuntu-24.04')
                self.assertEqual(calls, '')

    def test_selector_trust_and_timeouts(self):
        workflow = yaml.safe_load((ROOT / '.github/workflows/advisory-runner-select.yml').read_text())
        job = workflow['jobs']['select']
        self.assertIn('github.event.pull_request.head.repo.full_name == github.repository', job['runs-on'])
        self.assertEqual(job['outputs']['runner'], '${{ steps.select.outputs.runner || toJSON(inputs.hosted-runner) }}')
        checkout = job['steps'][0]
        self.assertEqual(checkout['id'], 'trusted-base')
        self.assertEqual(checkout['with']['ref'], '${{ github.event.pull_request.base.sha }}')
        self.assertFalse(checkout['with']['persist-credentials'])
        for step in job['steps']:
            self.assertIn("github.event_name == 'pull_request'", step['if'])
            self.assertIn('github.event.pull_request.head.repo.full_name == github.repository', step['if'])
            self.assertTrue(step['continue-on-error'])
            self.assertLessEqual(step['timeout-minutes'], 2)
        for step in job['steps'][1:]:
            self.assertIn("steps.trusted-base.outcome == 'success' &&", step['if'])

    def test_store_native_checks_support_unprivileged_heavy_runners(self):
        workflow = yaml.safe_load((ROOT / '.github/workflows/store-image-size.yml').read_text())
        steps = workflow['jobs']['measure']['steps']
        probe = next(step['run'] for step in steps if step.get('name') == 'Test measurement and health probe')
        self.assertIn('runner.environment', probe)
        self.assertIn('else\n  docker build --file scripts/ci/Dockerfile.native-runtime-test .', probe)
        self.assertIn('sudo python3 scripts/ci/test-runtime-native.py', probe)

    def test_smoke_automatic_ports_avoid_existing_host_listeners(self):
        script = ROOT / 'scripts/ci/smoke-serving-formats.sh'
        prefix = script.read_text().split('COMPOSE_FILE=', 1)[0]
        with socket.socket() as occupied:
            occupied.bind(('127.0.0.1', 0))
            env = {**os.environ, 'CI': 'true', 'GITHUB_ACTIONS': 'true'}
            for variable in ('HONUA_AOT_GEOPARQUET_PORT', 'HONUA_AOT_GEOPARQUET_POSTGRES_PORT'):
                env.pop(variable, None)
            command = prefix.replace('${BASH_SOURCE[0]}', '$0') + '\nprintf "%s %s" "$PORT" "$PG_PORT"\n'
            result = subprocess.run(['bash', '-eu', '-c', command, str(script), 'image', '/tmp', '0', '0'],
                                    env=env, capture_output=True, text=True, check=True)
            ports = [int(port) for port in result.stdout.split()]
            self.assertEqual(len(set(ports)), 2)
            self.assertNotIn(occupied.getsockname()[1], ports)
            for port in ports:
                self.assertGreater(port, 0)
                with socket.socket() as available:
                    available.bind(('127.0.0.1', port))

    def test_smoke_automatic_ports_retry_collisions(self):
        script = ROOT / 'scripts/ci/smoke-serving-formats.sh'
        prefix = script.read_text().split('COMPOSE_FILE=', 1)[0].replace('${BASH_SOURCE[0]}', '$0')
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            # Force a collision for both positional and environment-driven allocation.
            (path / 'python3').write_text(
                '#!/bin/sh\n'
                'case "$4" in *POSTGRES*) value=${HONUA_AOT_GEOPARQUET_POSTGRES_PORT:-0};; '
                '*) value=${HONUA_AOT_GEOPARQUET_PORT:-0};; esac\n'
                'if [ "$value" != 0 ]; then echo "$value"; exit 0; fi\ncount=0\n'
                'if [ -f "$PORT_CALL_MARKER" ]; then read -r count < "$PORT_CALL_MARKER"; fi\n'
                'count=$((count+1))\nprintf "%s\\n" "$count" > "$PORT_CALL_MARKER"\n'
                'if [ "$count" -le 2 ]; then echo 35189; else echo 35190; fi\n')
            (path / 'python3').chmod(0o755)
            command = prefix + '\nprintf "%s %s" "$PORT" "$PG_PORT"\n'
            cases = [
                (['', '35189'], {'CI': 'false'}, '35190 35189'),
                (['35189'], {'CI': 'false'}, '35189 35190'),
                ([], {'CI': 'false'}, '35190 35189'),
                (['0', '35189'], {}, '35190 35189'),
                (['35189', '0'], {}, '35189 35190'),
                (['0', '0'], {}, '35190 35189'),
                (['0', '0'], {'HONUA_AOT_GEOPARQUET_PORT': '35187', 'HONUA_AOT_GEOPARQUET_POSTGRES_PORT': '35188'}, '35190 35189'),
                ([], {'HONUA_AOT_GEOPARQUET_PORT': '0', 'HONUA_AOT_GEOPARQUET_POSTGRES_PORT': '0'}, '35190 35189'),
                ([], {'HONUA_AOT_GEOPARQUET_PORT': '0', 'HONUA_AOT_GEOPARQUET_POSTGRES_PORT': '35189'}, '35190 35189'),
                ([], {'HONUA_AOT_GEOPARQUET_PORT': '35189', 'HONUA_AOT_GEOPARQUET_POSTGRES_PORT': '0'}, '35189 35190'),
            ]
            for arguments, overrides, expected in cases:
                with self.subTest(arguments=arguments, environment=overrides):
                    (path / 'called').unlink(missing_ok=True)
                    env = {**os.environ, 'CI': 'true', 'PATH': f'{path}:{os.environ["PATH"]}',
                           'PORT_CALL_MARKER': str(path / 'called')}
                    for variable in ('HONUA_AOT_GEOPARQUET_PORT', 'HONUA_AOT_GEOPARQUET_POSTGRES_PORT'):
                        env.pop(variable, None)
                    env.update(overrides)
                    result = subprocess.run(
                        ['bash', '-eu', '-c', command, str(script), 'image', '/tmp', *arguments],
                        env=env, capture_output=True, text=True, check=True, timeout=5)
                    self.assertEqual(result.stdout, expected)

    def test_store_scans_use_the_selected_docker_socket(self):
        workflow = yaml.safe_load((ROOT / '.github/workflows/store-image-size.yml').read_text())
        scan = next(step['run'] for step in workflow['jobs']['measure']['steps']
                    if step.get('name') == 'Compare vulnerability findings')
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / 'docker').write_text('#!/bin/sh\nprintf "%s\\n" "$*" >> "$MOCK_CALLS"\necho "{}"\n')
            (path / 'docker').chmod(0o755)
            # Exercise the actual scan loop, excluding the JSON comparison.
            loop = scan.split("python3 - <<'PYTHON'", 1)[0]
            (path / 'artifacts').mkdir()
            for host, expected in [('', '/var/run/docker.sock'),
                                   ('unix:///run/user/1001/docker.sock', '/run/user/1001/docker.sock')]:
                calls = path / 'calls'
                calls.unlink(missing_ok=True)
                env = {**os.environ, 'PATH': f'{path}:{os.environ["PATH"]}',
                       'DOCKER_HOST': host, 'MOCK_CALLS': str(calls), 'BASELINE': 'baseline',
                       'IMAGE': 'honua-store-measure:fixture-run-attempt-profile-compilation'}
                subprocess.run(['bash', '-eu', '-c', loop], cwd=path, env=env, check=True)
                self.assertEqual(calls.read_text().count(f'-v {expected}:/var/run/docker.sock'), 2)
                commands = calls.read_text().splitlines()
                self.assertEqual(len(commands), 2)
                self.assertTrue(commands[0].endswith(' baseline'))
                self.assertTrue(commands[1].endswith(' ' + env['IMAGE']))

    def test_all_callers_preserve_analysis_and_fallback(self):
        cases = [('codeql.yml', 'analyze', 'ubuntu-latest'),
                 ('store-image-size.yml', 'measure', 'ubuntu-24.04'),
                 ('serving-image-boundary.yml', 'build-and-verify', 'ubuntu-latest')]
        for name, job_name, hosted in cases:
            with self.subTest(workflow=name):
                workflow = yaml.safe_load((ROOT / '.github/workflows' / name).read_text())
                selector, job = workflow['jobs']['runner-select'], workflow['jobs'][job_name]
                self.assertEqual(selector['uses'], './.github/workflows/advisory-runner-select.yml')
                self.assertEqual(selector['with']['hosted-runner'], hosted)
                self.assertIn('runner-select', job['needs'])
                self.assertIn('always() && !cancelled()', job['if'])
                self.assertIn('!github.event.pull_request.draft)', job['if'])
                self.assertIn(f"'\"{hosted}\"'", job['runs-on'])
                self.assertIn('(!github.event.pull_request || github.event.pull_request.head.repo.full_name == github.repository) && fromJSON(', job['runs-on'])
                self.assertTrue(job['runs-on'].endswith(" || 'ubuntu-latest' }}"))
                self.assertTrue(job['steps'])
        serving = yaml.safe_load((ROOT / '.github/workflows/serving-image-boundary.yml').read_text())
        self.assertIn("needs.detect.result == 'success'", serving['jobs']['build-and-verify']['if'])


if __name__ == '__main__':
    unittest.main()
