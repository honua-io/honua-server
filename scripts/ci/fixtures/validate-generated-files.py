#!/usr/bin/env python3
"""Workflow contracts and real-Git dry-run/commit/race tests (no network/build)."""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
IDENTITY = ['-c', 'user.name=Mike McDougall', '-c', 'user.email=mike@honua.io']


class GeneratedFilesContracts(unittest.TestCase):
    def test_workflow_runs_only_on_trunk_and_never_loops(self):
        workflow = (ROOT / '.github/workflows/generated-files-on-trunk.yml').read_text()
        self.assertIn('  push:\n    branches: [trunk]', workflow)
        self.assertNotIn('pull_request:', workflow)
        self.assertNotIn('workflow_dispatch:', workflow)
        self.assertIn('cancel-in-progress: false', workflow)
        self.assertIn('ref: trunk', workflow)
        self.assertIn("!(startsWith(github.event.head_commit.message, 'ci: regenerate generated files on trunk')", workflow)
        self.assertIn("&& contains(github.event.head_commit.message, 'Generated-From: '))", workflow)
        self.assertIn('token: ${{ secrets.MERGE_TRAIN_TOKEN }}', workflow)
        self.assertLess(workflow.index('regenerate-generated-files.sh'), workflow.index('commit-generated-files.sh --commit'))
        self.assertNotIn('continue-on-error:', workflow)
        self.assertIn('regenerate-generated-files.sh --configuration Release /p:RunAnalyzers=false', workflow)

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


if __name__ == '__main__':
    unittest.main(verbosity=2)
