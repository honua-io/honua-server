#!/usr/bin/env python3
"""Workflow contracts and real-Git dry-run/commit/race tests (no network/build)."""
from pathlib import Path
import base64
import json
import os
import shutil
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[3]
IDENTITY = ['-c', 'user.name=Mike McDougall', '-c', 'user.email=mike@honua.io']


class GeneratedFilesContracts(unittest.TestCase):
    def test_workflow_runs_only_on_trunk_and_never_loops(self):
        workflow = (ROOT / '.github/workflows/generated-files-on-trunk.yml').read_text()
        self.assertIn('  push:\n    branches: [trunk]', workflow)
        self.assertNotIn('pull_request:', workflow)
        self.assertNotIn('workflow_dispatch:', workflow)
        self.assertIn('    concurrency:\n      group: generated-files-on-trunk\n      cancel-in-progress: false', workflow)
        self.assertLess(workflow.index('    if:'), workflow.index('    concurrency:'))
        self.assertIn('ref: trunk', workflow)
        self.assertIn('needs: provenance', workflow)
        self.assertIn("if: needs.provenance.outputs.generated != 'true'", workflow)
        self.assertLess(workflow.index('regenerate-generated-files.sh'), workflow.index('commit-generated-files.sh --commit'))
        self.assertNotIn('continue-on-error:', workflow)
        self.assertIn('regenerate-generated-files.sh --configuration Release /p:RunAnalyzers=false', workflow)

    def test_bypass_token_reaches_only_the_isolated_push_step(self):
        workflow = (ROOT / '.github/workflows/generated-files-on-trunk.yml').read_text()
        generator = workflow.split('\n  regenerate:\n', 1)[1].split('\n  publish:\n', 1)[0]
        writer = workflow.split('\n  publish:\n', 1)[1]
        # Generator/build/test code runs with the read-only GITHUB_TOKEN only.
        self.assertIn('regenerate-generated-files.sh', generator)
        self.assertNotIn('secrets.', generator)
        self.assertNotIn('token:', generator)
        self.assertIn('persist-credentials: false', generator)
        self.assertIn('package-generated-files.sh', generator)
        # The writer is a separate runner that executes no generator code.
        self.assertIn('needs: regenerate', writer)
        self.assertIn("if: needs.regenerate.outputs.changed == 'true'", writer)
        self.assertIn('persist-credentials: false', writer)
        for forbidden in ('setup-dotnet', 'dotnet ', 'regenerate-generated-files', 'python3', 'npm ', './.github/actions/'):
            self.assertNotIn(forbidden, writer)
        steps = writer.split('\n      - ')[1:]
        self.assertEqual(workflow.count('secrets.'), 1)
        self.assertTrue(all('secrets.' not in step for step in steps[:-1]))
        self.assertIn('GENERATED_FILES_PUSH_TOKEN: ${{ secrets.MERGE_TRAIN_TOKEN }}', steps[-1])
        self.assertIn('run: bash scripts/ci/commit-generated-files.sh --commit', steps[-1])
        self.assertIn('apply-generated-files.sh', steps[-2])

    def test_skip_requires_exact_subject_and_single_parent_provenance(self):
        workflow = (ROOT / '.github/workflows/generated-files-on-trunk.yml').read_text()
        script = textwrap.dedent(workflow.split('          script: |\n', 1)[1].split('\n  regenerate:', 1)[0])
        parent, other = 'a' * 40, 'b' * 40
        subject = 'ci: regenerate generated files on trunk'
        message = f'{subject}\n\nGenerated-From: {parent}\n\nRefs #3213'
        cases = [
            (message, [parent], True),
            (message, [other], False),  # Inherited squash/cherry-pick trailer.
            (message, [parent, other], False),
            (message, [], False),
            (subject, [parent], False),
            (message.replace(subject, subject + ' (#4540)'), [parent], False),
            (message.replace('Generated-From:', 'quoted Generated-From:'), [parent], False),
            (message + f'\nGenerated-From: {other}', [parent], False),
        ]
        for body, parents, expected in cases:
            with self.subTest(body=body, parents=parents):
                commit = {'message': body, 'parents': [{'sha': sha} for sha in parents]}
                harness = """
const assert = require('node:assert/strict');
const context = {repo: {owner: 'honua-io', repo: 'honua-server'}, sha: 'event-sha'};
const core = {setOutput: (key, value) => {assert.equal(key, 'generated'); console.log(value);}};
const github = {rest: {git: {getCommit: async args => {
  assert.deepEqual(args, {...context.repo, commit_sha: context.sha});
  return {data: COMMIT};
}}}};
(async () => {SCRIPT})().catch(error => {console.error(error); process.exit(1);});
""".replace('COMMIT', json.dumps(commit)).replace('SCRIPT', script)
                result = subprocess.run(['node', '-e', harness], text=True, capture_output=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout.strip(), str(expected).lower())

    def test_pr_generation_is_hard_and_precedes_strict_validators(self):
        action = (ROOT / '.github/actions/lean-gate/action.yml').read_text()
        generation = action.index('bash scripts/ci/regenerate-generated-files.sh')
        self.assertLess(generation, action.index('- name: Run .NET Tests (Server Fast Tier)'))
        self.assertLess(generation, action.index('- name: Run .NET Tests (Architecture)'))
        self.assertIn('regenerate-generated-files.sh --configuration Release --no-build --no-restore', action)
        self.assertNotIn('continue-on-error:', action)
        matrix = (ROOT / '.github/workflows/capability-matrix-aggregation.yml').read_text()
        self.assertIn('run: python3 scripts/ci/generate-capability-matrix.py', matrix)
        self.assertIn('report-generated-file-drift.sh docs/gis/data/capability-matrix.v1.json', matrix)
        self.assertIn('run: python3 scripts/ci/validate-cite-openapi-compliance.py', matrix)
        self.assertNotIn('exit 1', matrix)
        self.assertNotIn('continue-on-error:', matrix)
        ci = (ROOT / '.github/workflows/ci.yml').read_text()
        refresh = ci.index('- name: Regenerate foundation projections')
        self.assertLess(ci.index('- name: Build foundation family binaries'), refresh)
        self.assertLess(refresh, ci.index('- name: Run foundation family tests'))
        self.assertIn("if: matrix.family == 'server'", ci[refresh:ci.index('- name: Run foundation family tests')])
        generator = (ROOT / 'scripts/ci/regenerate-generated-files.sh').read_text()
        self.assertIn('set -euo pipefail', generator)
        self.assertLess(generator.index('generate-feature-catalog.sh'), generator.index('generate-capability-matrix.py'))
        self.assertLess(generator.index('generate-geoservices-parity.sh'), generator.index('generate-capability-matrix.py'))
        self.assertIn('verify-admin-operation-parity.py', generator)
        self.assertIn('generate-admin-operation-parity-exports.sh "$@" --no-build --no-restore', generator)
        self.assertIn('generate-geoservices-parity.sh "$@" --no-build --no-restore', generator)

    def test_generator_failure_stops_the_pipeline(self):
        with tempfile.TemporaryDirectory() as temp:
            repo = Path(temp)
            scripts = repo / 'scripts'
            (scripts / 'ci').mkdir(parents=True)
            shutil.copy(ROOT / 'scripts/ci/regenerate-generated-files.sh', scripts / 'ci')
            (scripts / 'generate-feature-catalog.sh').write_text('exit 23\n')
            (scripts / 'generate-admin-operation-parity-exports.sh').write_text('touch should-not-run\n')
            result = subprocess.run(['bash', 'scripts/ci/regenerate-generated-files.sh'], cwd=repo)
            self.assertEqual(result.returncode, 23)
            self.assertFalse((repo / 'should-not-run').exists())

    def test_real_git_noop_drift_identity_allowlist_replay_and_race(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            repo = base / 'repo'
            remote = base / 'origin.git'
            repo.mkdir()
            env = dict(os.environ, GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull)
            def run(*args, ok=True):
                result = subprocess.run(args, cwd=repo, env=env, text=True, capture_output=True)
                if ok:
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                return result
            run('git', 'init', '--bare', str(remote))
            run('git', 'init', '-b', 'trunk')
            scripts = repo / 'scripts/ci'
            scripts.mkdir(parents=True)
            for name in ('generated-files.sh', 'commit-generated-files.sh', 'report-generated-file-drift.sh'):
                shutil.copy(ROOT / 'scripts/ci' / name, scripts / name)
            paths = run('bash', '-c', 'source scripts/ci/generated-files.sh; printf "%s\\n" "${GENERATED_FILES[@]}"').stdout.splitlines()
            for name in [*paths, 'authored-input.json']:
                path = repo / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('{}\n')
            run('git', 'add', '.')
            run('git', *IDENTITY, 'commit', '-m', 'initial')
            run('git', 'remote', 'add', 'origin', str(remote))
            run('git', 'push', 'origin', 'trunk')
            original = run('git', 'rev-parse', 'HEAD').stdout.strip()
            self.assertIn('No generated changes', run('bash', 'scripts/ci/commit-generated-files.sh', '--commit').stdout)
            (repo / paths[0]).write_text('{"fresh": true}\n')
            (repo / 'authored-input.json').write_text('{"authored": true}\n')
            summary = base / 'summary.md'
            env['GITHUB_STEP_SUMMARY'] = str(summary)
            report = run('bash', 'scripts/ci/report-generated-file-drift.sh')
            self.assertIn('::notice::', report.stdout)
            self.assertIn(paths[0], summary.read_text())
            self.assertNotIn('authored-input.json', report.stdout)
            self.assertIn('Dry run:', run('bash', 'scripts/ci/commit-generated-files.sh', '--dry-run').stdout)
            self.assertEqual(run('git', 'rev-parse', 'HEAD').stdout.strip(), original)
            run('git', 'add', 'authored-input.json')
            self.assertNotEqual(run('bash', 'scripts/ci/commit-generated-files.sh', '--commit', ok=False).returncode, 0)
            run('git', 'restore', '--staged', 'authored-input.json')
            # Ambient identity cannot override the required author/committer.
            env.update(GIT_AUTHOR_NAME='Wrong', GIT_AUTHOR_EMAIL='wrong@example.com',
                       GIT_COMMITTER_NAME='Wrong', GIT_COMMITTER_EMAIL='wrong@example.com')
            run('bash', 'scripts/ci/commit-generated-files.sh', '--commit')
            self.assertEqual(run('git', 'show', '--format=%an <%ae>|%cn <%ce>', '--no-patch').stdout.strip(),
                             'Mike McDougall <mike@honua.io>|Mike McDougall <mike@honua.io>')
            self.assertEqual(run('git', 'diff-tree', '--no-commit-id', '--name-only', '-r', 'HEAD').stdout.strip(), paths[0])
            published = run('git', 'rev-parse', 'HEAD').stdout.strip()
            self.assertEqual(run('git', '--git-dir', str(remote), 'rev-parse', 'refs/heads/trunk').stdout.strip(), published)
            self.assertIn('No generated changes', run('bash', 'scripts/ci/commit-generated-files.sh', '--commit').stdout)
            self.assertEqual(run('git', 'rev-parse', 'HEAD').stdout.strip(), published)
            # Simulate a newer merge while this checkout is still generating.
            run('git', 'commit', '--allow-empty', '-m', 'concurrent merge')
            newer = run('git', 'rev-parse', 'HEAD').stdout.strip()
            run('git', 'push', 'origin', 'trunk')
            run('git', 'reset', '--soft', published)
            (repo / paths[0]).write_text('{"stale": true}\n')
            result = run('bash', 'scripts/ci/commit-generated-files.sh', '--commit', ok=False)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('rejected', result.stderr)
            self.assertEqual(run('git', '--git-dir', str(remote), 'rev-parse', 'refs/heads/trunk').stdout.strip(), newer)

    def test_writer_accepts_only_allowlisted_bundle_and_scopes_token_to_push(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            generator, writer, remote = base / 'generator', base / 'writer', base / 'origin.git'
            env = dict(os.environ, GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull)
            env.pop('GITHUB_OUTPUT', None)
            def run(cwd, *args, ok=True, extra=None):
                result = subprocess.run(args, cwd=cwd, env={**env, **(extra or {})}, text=True, capture_output=True)
                if ok:
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                return result
            run(base, 'git', 'init', '--bare', str(remote))
            generator.mkdir()
            run(generator, 'git', 'init', '-b', 'trunk')
            (generator / 'scripts/ci').mkdir(parents=True)
            for name in ('generated-files.sh', 'package-generated-files.sh', 'apply-generated-files.sh', 'commit-generated-files.sh'):
                shutil.copy(ROOT / 'scripts/ci' / name, generator / 'scripts/ci' / name)
            paths = run(generator, 'bash', '-c', 'source scripts/ci/generated-files.sh; printf "%s\\n" "${GENERATED_FILES[@]}"').stdout.splitlines()
            for name in paths:
                (generator / name).parent.mkdir(parents=True, exist_ok=True)
                (generator / name).write_text('{}\n')
            run(generator, 'git', 'add', '.')
            run(generator, 'git', *IDENTITY, 'commit', '-m', 'initial')
            run(generator, 'git', 'remote', 'add', 'origin', str(remote))
            run(generator, 'git', 'push', 'origin', 'trunk')
            run(base, 'git', 'clone', '-b', 'trunk', str(remote), str(writer))
            source = run(generator, 'git', 'rev-parse', 'HEAD').stdout.strip()

            bundle = base / 'bundle'
            outputs = base / 'outputs'
            run(generator, 'bash', 'scripts/ci/package-generated-files.sh', str(bundle), extra={'GITHUB_OUTPUT': str(outputs)})
            self.assertIn('changed=false', outputs.read_text())
            (generator / paths[0]).write_text('{"fresh": true}\n')
            run(generator, 'bash', 'scripts/ci/package-generated-files.sh', str(bundle), extra={'GITHUB_OUTPUT': str(outputs)})
            self.assertIn('changed=true', outputs.read_text().splitlines()[-1])
            self.assertEqual((bundle / 'source-sha').read_text().strip(), source)

            def tampered(mutate):
                copy = base / 'tampered'
                shutil.rmtree(copy, ignore_errors=True)
                shutil.copytree(bundle, copy, symlinks=True)
                mutate(copy)
                result = run(writer, 'bash', 'scripts/ci/apply-generated-files.sh', str(copy), ok=False)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('Rejected generator bundle', result.stderr)
                self.assertEqual(run(writer, 'git', 'status', '--porcelain').stdout, '')
            # Outputs outside the allowlist (e.g. a workflow) never reach trunk.
            tampered(lambda b: (b / 'files/.github/workflows').mkdir(parents=True) or (b / 'files/.github/workflows/x.yml').write_text('x'))
            tampered(lambda b: (b / 'files' / paths[1]).unlink())
            tampered(lambda b: (b / 'files' / paths[1]).unlink() or (b / 'files' / paths[1]).symlink_to('/etc/passwd'))
            # A claimed descendant of trunk could fast-forward unreviewed commits.
            tampered(lambda b: (b / 'source-sha').write_text('c' * 40 + '\n'))
            tampered(lambda b: (b / 'source-sha').write_text('not-a-sha\n'))

            run(writer, 'bash', 'scripts/ci/apply-generated-files.sh', str(bundle))
            self.assertEqual(run(writer, 'git', 'status', '--porcelain').stdout.strip(), f'M {paths[0]}')

            # Record the git config each git subprocess sees; only push may carry the token.
            shim = base / 'bin'
            shim.mkdir()
            real_git = shutil.which('git')
            log = base / 'git-env.log'
            (shim / 'git').write_text(f'#!/usr/bin/env bash\necho "$1 ${{GIT_CONFIG_KEY_0:-}}=${{GIT_CONFIG_VALUE_0:-}}" >> {log}\nexec {real_git} "$@"\n')
            (shim / 'git').chmod(0o755)
            token = 'fake-bypass-token'
            run(writer, 'bash', 'scripts/ci/commit-generated-files.sh', '--commit',
                extra={'GENERATED_FILES_PUSH_TOKEN': token, 'PATH': f'{shim}:{env["PATH"]}'})
            header = 'AUTHORIZATION: basic ' + base64.b64encode(f'x-access-token:{token}'.encode()).decode()
            calls = log.read_text().splitlines()
            self.assertIn(f'push http.https://github.com/.extraheader={header}', calls)
            self.assertTrue(all(line.endswith(' =') for line in calls if not line.startswith('push ')))
            published = run(writer, 'git', 'rev-parse', 'HEAD').stdout.strip()
            self.assertEqual(run(writer, 'git', 'rev-parse', 'HEAD^').stdout.strip(), source)
            self.assertEqual(run(base, 'git', '--git-dir', str(remote), 'rev-parse', 'refs/heads/trunk').stdout.strip(), published)
            for path in (writer / '.git').rglob('*'):
                if path.is_file():
                    data = path.read_bytes()
                    self.assertNotIn(token.encode(), data, path)
                    self.assertNotIn(header.encode(), data, path)


if __name__ == '__main__':
    unittest.main(verbosity=2)
