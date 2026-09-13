#!/usr/bin/env python3
"""Workflow contracts and real-Git no-op/publish/branch-reuse tests (no network/build)."""
from pathlib import Path
import os
import shutil
import stat
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]


class GeneratedFilesContracts(unittest.TestCase):
    def test_workflow_runs_only_on_trunk_push_and_never_writes_trunk_directly(self):
        workflow = (ROOT / '.github/workflows/generated-files-on-trunk.yml').read_text()
        self.assertIn('  push:\n    branches: [trunk]', workflow)
        self.assertNotIn('pull_request:', workflow)
        self.assertNotIn('workflow_dispatch:', workflow)
        self.assertIn('concurrency:\n  group: generated-files-on-trunk\n  cancel-in-progress: false', workflow)
        self.assertIn('  contents: read', workflow)
        self.assertNotIn('  contents: write', workflow)
        self.assertIn('ref: ${{ github.sha }}', workflow)
        self.assertIn('persist-credentials: false', workflow)
        self.assertNotIn('ref: trunk', workflow)
        self.assertLess(
            workflow.index('regenerate-generated-files.sh'),
            workflow.index('publish-generated-files.sh'),
        )
        self.assertIn('regenerate-generated-files.sh --configuration Release /p:RunAnalyzers=false', workflow)
        publication = workflow.index('- name: Open or refresh')
        self.assertNotIn('secrets.', workflow[:publication])
        self.assertIn('GH_TOKEN: ${{ secrets.MERGE_TRAIN_TOKEN }}', workflow[publication:])
        self.assertNotIn('GH_TOKEN: ${{ github.token }}', workflow)

        script = (ROOT / 'scripts/ci/publish-generated-files.sh').read_text()
        executable_lines = [line for line in script.splitlines() if not line.strip().startswith('#')]
        self.assertFalse(any('push' in line and 'refs/heads/trunk' in line for line in executable_lines))
        self.assertIn("branch='automation/regenerate-generated-files'", script)
        self.assertIn('gh pr create', script)
        self.assertIn('--force-with-lease', script)

    def test_publish_script_push_target_passes_the_merge_authority_guard(self):
        # The exact regression this design fixes (#4540/#4653): a workflow
        # that writes trunk directly, or whose push target is a variable the
        # guard cannot statically prove safe, fails
        # scripts/ci/validate-single-merge-authority.sh. Prove the real
        # repository tree -- including this new script -- still passes both
        # the guard's offline self-test and its real scan.
        script = (ROOT / 'scripts/ci/publish-generated-files.sh').read_text()
        self.assertIn('force-with-lease', script)
        self.assertIn('automation/regenerate-generated-files', script)
        result = subprocess.run(
            ['bash', str(ROOT / 'scripts/ci/validate-single-merge-authority.sh'), '--self-test'],
            cwd=ROOT, capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        result = subprocess.run(
            ['bash', str(ROOT / 'scripts/ci/validate-single-merge-authority.sh')],
            cwd=ROOT, capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

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

    def test_real_git_publish_creates_then_reuses_the_branch(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            repo = base / 'repo'
            remote = base / 'origin.git'
            repo.mkdir()
            env = dict(os.environ, GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull)
            env.pop('GITHUB_TOKEN', None)
            env['GH_TOKEN'] = 'test-publication-token'

            gh_calls = base / 'gh-calls.log'
            gh_state = base / 'gh-pr-number'
            fake_bin = base / 'bin'
            fake_bin.mkdir()
            fake_gh = fake_bin / 'gh'
            fake_gh.write_text(
                '#!/usr/bin/env bash\n'
                f'{{ echo "$*"; cat; }} >> {gh_calls}\n'
                'if [[ "$1 $2" == "pr list" ]]; then\n'
                f'  cat {gh_state} 2>/dev/null || true\n'
                '  exit 0\n'
                'fi\n'
                'if [[ "$1 $2" == "pr create" ]]; then\n'
                f'  echo 42 > {gh_state}\n'
                '  echo "https://example.invalid/pull/42"\n'
                '  exit 0\n'
                'fi\n'
                'echo "unexpected gh invocation: $*" >&2\n'
                'exit 1\n'
            )
            fake_gh.chmod(fake_gh.stat().st_mode | stat.S_IEXEC)
            env['PATH'] = f'{fake_bin}:{env["PATH"]}'

            def run(*args, ok=True):
                result = subprocess.run(args, cwd=repo, env=env, text=True, capture_output=True)
                if ok:
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                return result

            run('git', 'init', '--bare', str(remote))
            run('git', 'init', '-b', 'trunk')
            scripts = repo / 'scripts/ci'
            scripts.mkdir(parents=True)
            for name in ('generated-files.sh', 'publish-generated-files.sh'):
                shutil.copy(ROOT / 'scripts/ci' / name, scripts / name)
            paths = run('bash', '-c', 'source scripts/ci/generated-files.sh; printf "%s\\n" "${GENERATED_FILES[@]}"').stdout.splitlines()
            for name in paths:
                path = repo / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('{}\n')
            run('git', 'add', '.')
            run('git', '-c', 'user.name=Mike McDougall', '-c', 'user.email=mike@honua.io', 'commit', '-m', 'initial')
            run('git', 'remote', 'add', 'origin', str(remote))
            run('git', 'push', 'origin', 'trunk')
            trunk_before = run('git', '--git-dir', str(remote), 'rev-parse', 'refs/heads/trunk').stdout.strip()

            # No diff: no commit, no branch push, no gh call at all.
            noop = run('bash', 'scripts/ci/publish-generated-files.sh')
            self.assertIn('up to date', noop.stdout)
            self.assertFalse(gh_calls.exists())
            self.assertEqual(run('git', 'rev-parse', 'HEAD').stdout.strip(), trunk_before)

            # A real diff: commits on the fixed branch, pushes only that branch
            # (never trunk), and opens a PR since none is open yet.
            (repo / paths[0]).write_text('{"fresh": true}\n')
            first = run('bash', 'scripts/ci/publish-generated-files.sh')
            self.assertNotIn('Updated existing', first.stdout)

            # The branch push landed, trunk did not move.
            branch_sha = run('git', '--git-dir', str(remote), 'rev-parse',
                              'refs/heads/automation/regenerate-generated-files').stdout.strip()
            self.assertEqual(
                run('git', '--git-dir', str(remote), 'rev-parse', 'refs/heads/trunk').stdout.strip(),
                trunk_before,
            )
            commit_identity = run('git', 'show', '--format=%an <%ae>|%cn <%ce>', '--no-patch', branch_sha).stdout.strip()
            bot_identity = 'Mike McDougall <mike@honua.io>'
            self.assertEqual(commit_identity, f'{bot_identity}|{bot_identity}')
            self.assertEqual(
                run('git', 'diff-tree', '--no-commit-id', '--name-only', '-r', branch_sha).stdout.strip(),
                paths[0],
            )
            gh_call_lines = gh_calls.read_text().splitlines()
            self.assertTrue(any(line.startswith('pr list') for line in gh_call_lines))
            create_call = next(line for line in gh_call_lines if line.startswith('pr create'))
            self.assertIn('--base trunk', create_call)
            self.assertIn('automation/regenerate-generated-files', create_call)
            self.assertIn('Refs #3213', create_call)

            self.assertIn(f'Generated-From: {trunk_before}',
                          run('git', 'log', '-1', '--format=%B', branch_sha).stdout)

            # A second publish with the same drift (e.g. the automation PR's
            # own eventual merge re-triggering the workflow with byte-identical
            # output) finds the already-open PR and updates the branch instead
            # of creating a duplicate.
            gh_calls.unlink()
            # Simulate the next run's fresh `actions/checkout` of trunk, which
            # never moved: the previous publish advanced only the local branch
            # this checkout happened to be on, not remote trunk.
            run('git', 'reset', '--hard', trunk_before)
            (repo / paths[0]).write_text('{"fresh": true}\n')
            second = run('bash', 'scripts/ci/publish-generated-files.sh')
            self.assertIn('Updated existing generated-files PR #42', second.stdout)
            self.assertNotIn('pr create', gh_calls.read_text())
            self.assertEqual(
                run('git', '--git-dir', str(remote), 'rev-parse', 'refs/heads/trunk').stdout.strip(),
                trunk_before,
            )

            # Advance trunk independently, then replay the old triggering SHA.
            # Its fresh validation may pass, but publication must preserve the
            # existing automation branch and make no PR calls.
            run('git', 'reset', '--hard', trunk_before)
            (repo / 'authored.txt').write_text('new source\n')
            run('git', 'add', 'authored.txt')
            run('git', '-c', 'user.name=Mike McDougall', '-c', 'user.email=mike@honua.io',
                'commit', '-m', 'advance trunk')
            run('git', 'push', 'origin', 'HEAD:trunk')
            run('git', 'reset', '--hard', trunk_before)
            (repo / paths[0]).write_text('{"stale": true}\n')
            branch_before = run('git', '--git-dir', str(remote), 'rev-parse',
                                'refs/heads/automation/regenerate-generated-files').stdout
            gh_calls.unlink()
            stale = run('bash', 'scripts/ci/publish-generated-files.sh')
            self.assertIn('No stale projections published', stale.stdout)
            self.assertFalse(gh_calls.exists())
            self.assertEqual(branch_before, run('git', '--git-dir', str(remote), 'rev-parse',
                                               'refs/heads/automation/regenerate-generated-files').stdout)
            self.assertEqual(run('git', 'rev-parse', 'HEAD').stdout.strip(), trunk_before)

            # A newer publisher wins after our trunk observation. The lease
            # must have been captured before that observation, so this push
            # fails instead of overwriting the newer automation head.
            current_trunk = run('git', '--git-dir', str(remote), 'rev-parse', 'refs/heads/trunk').stdout.strip()
            run('git', 'reset', '--hard', current_trunk)
            (repo / paths[0]).write_text('{"racing": true}\n')
            real_git = shutil.which('git')
            fake_git = fake_bin / 'git'
            fake_git.write_text(
                '#!/usr/bin/env bash\n'
                'if [[ "$*" == "ls-remote --exit-code origin refs/heads/trunk" ]]; then\n'
                f'  "{real_git}" "$@" || exit $?\n'
                f'  "{real_git}" push --force origin {current_trunk}:refs/heads/automation/regenerate-generated-files >&2\n'
                '  exit $?\n'
                'fi\n'
                f'exec "{real_git}" "$@"\n'
            )
            fake_git.chmod(fake_git.stat().st_mode | stat.S_IEXEC)
            raced = run('bash', 'scripts/ci/publish-generated-files.sh', ok=False)
            self.assertNotEqual(raced.returncode, 0, raced.stdout + raced.stderr)
            self.assertIn('stale info', raced.stderr)
            self.assertFalse(gh_calls.exists())
            self.assertEqual(current_trunk, run('git', '--git-dir', str(remote), 'rev-parse',
                                               'refs/heads/automation/regenerate-generated-files').stdout.strip())
            fake_git.unlink()
            # Restore a working-tree diff for the unreadable-remote case.
            (repo / paths[0]).write_text('{"unreachable": true}\n')

            # An unreachable origin must fail closed, not look like an absent
            # branch that permits an unconditional overwrite.
            run('git', 'remote', 'set-url', 'origin', str(base / 'missing.git'))
            failed = run('bash', 'scripts/ci/publish-generated-files.sh', ok=False)
            self.assertNotEqual(failed.returncode, 0)
            self.assertFalse(gh_calls.exists())

if __name__ == '__main__':
    unittest.main(verbosity=2)
