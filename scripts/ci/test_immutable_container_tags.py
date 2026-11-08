"""Offline coverage for immutable candidate image publication."""
import contextlib
import subprocess
import tempfile
import importlib.util
import json
import itertools
import os
import re
import sys
from pathlib import Path
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('immutable_container_tags', Path(__file__).with_name('immutable-container-tags.py'))


class ImmutableTagsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.module = importlib.util.module_from_spec(SPEC)
        SPEC.loader.exec_module(cls.module)

    def setUp(self):
        profile = patch.object(self.module, 'supports_full_variant', return_value=True)
        profile.start()
        self.addCleanup(profile.stop)

    def test_legacy_candidate_does_not_require_full_variant_tags(self):
        with patch.object(self.module, 'supports_full_variant', return_value=False), patch.object(
            self.module, 'inspect', side_effect=lambda reference: None if '-full' in reference else 'sha256:' + 'a' * 64
        ):
            self.assertEqual(self.module.publication_state('ghcr.io/honua/server', 'abcdef0' + 'a' * 33, ''),
                             (True, True))

    def test_cli_filter_and_digest_lookup_work_without_a_git_repository(self):
        script = Path(__file__).with_name('immutable-container-tags.py').resolve()
        with tempfile.TemporaryDirectory() as directory:
            stub = Path(directory) / 'docker'
            stub.write_text('#!/bin/sh\nprintf "manifest unknown\\n" >&2\nexit 1\n')
            stub.chmod(0o755)
            environment = {**os.environ, 'PATH': directory + os.pathsep + os.environ['PATH']}
            for arguments, tags, expected in [([], 'ghcr.io/honua/server:latest\n', 'ghcr.io/honua/server:latest'),
                    (['--tag', 'ghcr.io/honua/server:nightly-aaaaaaa'], '', '')]:
                with self.subTest(arguments=arguments):
                    result = subprocess.run([sys.executable, str(script), '--sha', 'a' * 40, *arguments],
                        cwd=directory, env=environment, input=tags, text=True, capture_output=True, timeout=10)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(result.stdout.strip(), expected)

    def test_existing_sha_tags_are_preserved_but_floating_tags_can_move(self):
        tags = ['ghcr.io/honua-io/honua-server:nightly-abcdef0',
                'ghcr.io/honua-io/honua-server:nightly-lambda-aot-abcdef0-amd64',
                'ghcr.io/honua-io/honua-server:nightly-compose-executor-abcdef0',
                'ghcr.io/honua-io/honua-server:nightly']
        with patch.object(self.module, 'inspect', return_value='sha256:' + 'a' * 64):
            self.assertEqual(self.module.filter_tags(tags, 'abcdef0' + 'a' * 33, ''), tags[-1:])

    def test_absent_sha_tags_can_be_created(self):
        tags = ['ghcr.io/honua-io/honua-server:nightly-jit-abcdef0-arm64']
        with patch.object(self.module, 'inspect', return_value=None):
            self.assertEqual(self.module.filter_tags(tags, 'abcdef0' + 'a' * 33, ''), tags)

    def test_explicit_release_mismatch_requires_new_sha(self):
        with patch.object(self.module, 'inspect', return_value='sha256:' + 'a' * 64), \
             patch.object(self.module, 'versions', return_value={'2026.1.0-nightly.gabcdef0'}):
            with self.assertRaisesRegex(ValueError, 'new SHA'):
                self.module.filter_tags(['ghcr.io/honua/server:nightly-abcdef0'], 'abcdef0' + 'a' * 33, '2026.1.0-rc.2')

    def test_matching_release_preserves_existing_digest(self):
        with patch.object(self.module, 'inspect', return_value='sha256:' + 'a' * 64), \
             patch.object(self.module, 'versions', return_value={'2026.1.0-rc.2'}):
            self.assertEqual(self.module.filter_tags(['ghcr.io/honua/server:nightly-abcdef0'], 'abcdef0' + 'a' * 33, '2026.1.0-rc.2'), [])

    def test_auth_and_network_errors_are_not_treated_as_absence(self):
        for message in ('unauthorized', '403 Forbidden', 'TLS handshake timeout', 'no such host'):
            with self.subTest(message=message), patch.object(self.module.subprocess, 'run') as run:
                run.return_value.returncode = 1
                run.return_value.stderr = message
                with self.assertRaises(RuntimeError):
                    self.module.inspect('ghcr.io/honua/server:nightly-abcdef0')

    def test_complete_registry_publication_does_not_depend_on_run_success_or_ref(self):
        with patch.object(self.module, 'inspect', return_value='sha256:' + 'a' * 64):
            self.assertTrue(self.module.already_imaged('ghcr.io/honua/server', 'abcdef0' + 'a' * 33, ''))
        with patch.object(self.module, 'inspect', side_effect=itertools.chain([None], itertools.repeat('sha256:' + 'a' * 64))):
            self.assertFalse(self.module.already_imaged('ghcr.io/honua/server', 'abcdef0' + 'a' * 33, ''))

    def test_missing_tag_is_distinct_from_inspection_failure(self):
        with patch.object(self.module.subprocess, 'run') as run:
            run.return_value.returncode = 1
            run.return_value.stderr = 'ERROR: ghcr.io/honua/server:nightly-abcdef0: not found'
            self.assertIsNone(self.module.inspect('ghcr.io/honua/server:nightly-abcdef0'))

    def test_explicit_release_checks_partial_publication_too(self):
        with patch.object(self.module, 'inspect', side_effect=[None, 'sha256:' + 'a' * 64]), \
             patch.object(self.module, 'versions', return_value={'2026.1.0-nightly.gabcdef0'}):
            with self.assertRaisesRegex(ValueError, 'new SHA'):
                self.module.already_imaged('ghcr.io/honua/server', 'abcdef0' + 'a' * 33, '2026.1.0-rc.2')

    def test_partial_publication_is_reported_for_safe_legacy_handoffs(self):
        with patch.object(self.module, 'inspect', side_effect=itertools.chain([None], itertools.repeat('sha256:' + 'a' * 64))):
            self.assertEqual(self.module.publication_state('ghcr.io/honua/server', 'abcdef0' + 'a' * 33, ''), (False, True))

    def test_missing_full_variant_is_partial_publication(self):
        repository = 'ghcr.io/honua/server'
        for suffix in ('', '-amd64', '-arm64'):
            missing = f'{repository}:nightly-jit-abcdef0-full{suffix}'
            with self.subTest(tag=missing), patch.object(
                self.module, 'inspect', side_effect=lambda reference: None if reference == missing else 'sha256:' + 'a' * 64
            ):
                self.assertEqual(
                    self.module.publication_state(repository, 'abcdef0' + 'a' * 33, ''),
                    (False, True))

    def test_scheduled_partial_retry_cannot_mix_rc_and_nightly_images(self):
        with patch.object(self.module, 'inspect', side_effect=itertools.cycle([None] + ['sha256:' + 'a' * 64] * 11)), \
             patch.object(self.module, 'versions', return_value={'2026.1.0-rc.2'}):
            with self.assertRaisesRegex(ValueError, 'new SHA'):
                self.module.check_publication(['ghcr.io/honua/server'], 'abcdef0' + 'a' * 33, '', '2026.1.0-nightly.gabcdef0')

    def test_every_platform_must_report_requested_version(self):
        with patch.object(self.module.subprocess, 'run') as run:
            run.return_value.stdout = json.dumps({
                'linux/amd64': {'config': {'Labels': {self.module.VERSION_LABEL: '2026.1.0-rc.2'}}},
                'linux/arm64': {'config': {'Labels': {}}}})
            self.assertEqual(self.module.versions('image@digest'), {'2026.1.0-rc.2', None})

    def test_every_job_that_runs_the_guard_has_the_guard_on_disk(self):
        workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/nightly-container-build.yml').read_text()
        jobs = re.split(r'\n  (?=[a-z][a-z0-9-]*:\n)', workflow.split('\njobs:\n', 1)[1])
        for block in jobs:
            name = block.split(':', 1)[0]
            if 'scripts/ci/immutable-container-tags.py' not in block:
                continue
            with self.subTest(job=name):
                if '--already-imaged' in block:
                    self.assertIn('fetch-depth: 0', block)
                self.assertTrue('actions/checkout@' in block,
                                f'{name} runs the immutable-tag guard without checking it out')

    def test_workflow_guards_every_publication_path_and_serializes_same_sha(self):
        root = Path(__file__).resolve().parents[2]
        workflow = (root / '.github/workflows/nightly-container-build.yml').read_text()
        self.assertIn("steps.route.outputs.publish == 'true' && steps.existing.outputs.imaged != '1'", workflow)
        self.assertLess(workflow.index('name: Check immutable candidate images'),
                        workflow.index('name: Require the release GitHub App'))
        self.assertIn('--commit "${CANDIDATE_SHA}" --limit 100 --json status', workflow)
        self.assertNotIn('select(.status == \"completed\" and .conclusion == \"success\"', workflow)
        for job in ('build-aot', 'build-lambda-aot', 'build-jit', 'manifest-aot',
                    'manifest-lambda-aot', 'manifest-jit', 'compose-executor', 'verify-functions-aot'):
            with self.subTest(job=job):
                block = re.split(r'\n  [a-z][a-z0-9-]*:', workflow.split('\n  ' + job + ':\n', 1)[1], maxsplit=1)[0]
                self.assertIn('concurrency:', block)
                self.assertIn('group: nightly-publish-${{ needs.select-candidate.outputs.sha }}', block)
        self.assertEqual(workflow.count('| python3 scripts/ci/promote-verified-image.py'), 3)
        self.assertEqual(workflow.count('scripts/ci/immutable-container-tags.py --sha "$CANDIDATE_SHA" --release-version "$RELEASE_VERSION")"'), 4)
        self.assertIn('tags: ${{ steps.immutable_tags.outputs.tags }}', workflow)
        self.assertIn("if: steps.existing_executor.outputs.digest == ''", workflow)
        self.assertIn('digest=${EXISTING_DIGEST:-$BUILT_DIGEST}', workflow)
        self.assertIn('subject_ref="${subject_name}:nightly-${{ needs.select-candidate.outputs.sha7 }}"', workflow)
        self.assertIn('subject_ref="${subject_name}:nightly-lambda-aot-${{ needs.select-candidate.outputs.sha7 }}"', workflow)
        # Existing Git candidate refs are accepted only when their full SHA matches.
        self.assertIn('if [ "${existing}" != "${CANDIDATE_SHA}" ]; then', workflow)
        self.assertIn('if [ "$PARTIAL_PUBLICATION" = 1 ]; then', workflow)
        self.assertIn('contents/scripts/ci/immutable-container-tags.py?ref=${CANDIDATE_SHA}', workflow)


class CandidateProfileTests(unittest.TestCase):
    def test_profile_detection_uses_the_candidate_tree_and_refuses_unknown_commits(self):
        module = importlib.util.module_from_spec(SPEC)
        SPEC.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as directory, contextlib.chdir(directory):
            subprocess.run(['git', 'init', '-q'], check=True)
            subprocess.run(['git', 'config', 'user.name', 'Mike McDougall'], check=True)
            subprocess.run(['git', 'config', 'user.email', 'mike@honua.io'], check=True)
            profile = Path('eng/Honua.BuildProfiles.props')
            profile.parent.mkdir()
            profile.write_text('<Project/>')
            subprocess.run(['git', 'add', '.'], check=True)
            subprocess.run(['git', 'commit', '-qm', 'base profile'], check=True)
            previous = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
            profile.write_text('<Project><PropertyGroup><HonuaStoreProfile>full</HonuaStoreProfile></PropertyGroup></Project>')
            subprocess.run(['git', 'add', '.'], check=True)
            subprocess.run(['git', 'commit', '-qm', 'store profile'], check=True)
            self.assertFalse(module.supports_full_variant(previous))
            self.assertTrue(module.supports_full_variant('HEAD'))
            with self.assertRaisesRegex(RuntimeError, 'Cannot read candidate'):
                module.supports_full_variant('0' * 40)


if __name__ == '__main__':
    unittest.main()
