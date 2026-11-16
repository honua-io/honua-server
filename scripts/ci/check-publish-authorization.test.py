#!/usr/bin/env python3
"""Publishing jobs must be authorized by ref and name an environment."""
from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import unittest
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('publish_authorization', ROOT / 'scripts/ci/check-publish-authorization.py')
check = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(check)

PUSH = {'uses': 'docker/build-push-action@v7', 'with': {'push': True, 'tags': 'ghcr.io/honua-io/honua-server:x'}}
AUTHORIZE = {'uses': check.AUTHORIZER}


def publisher(**fields) -> dict:
    return {'runs-on': 'ubuntu-24.04', 'steps': [PUSH], **fields}


def problems(jobs: dict, exceptions: dict | None = None) -> list[str]:
    return check.findings({'release.yml': {'jobs': jobs}}, exceptions or {})


class PublishAuthorizationTests(unittest.TestCase):
    def test_a_publisher_behind_the_authorization_job_with_an_environment_passes(self) -> None:
        jobs = {'authorize-publish': AUTHORIZE,
                'build': publisher(needs='authorize-publish', environment='publish-images')}
        self.assertEqual(problems(jobs), [])

    def test_authorization_is_inherited_through_needs(self) -> None:
        jobs = {'authorize-publish': AUTHORIZE,
                'build': {'needs': ['authorize-publish'], 'steps': [{'run': 'make'}]},
                'publish': publisher(needs=['build'], environment='publish-images')}
        self.assertEqual(problems(jobs), [])

    def test_a_trunk_condition_alone_does_not_authorize(self) -> None:
        # GitHub compares strings without case, so the condition is also true on a branch named TRUNK;
        # the shared job compares the ref exactly.
        jobs = {'publish': publisher(**{'if': "github.ref == 'refs/heads/trunk'"}, environment='publish-images')}
        self.assertEqual(problems(jobs), ['release.yml:publish: publishing job without authorization'])

    def test_a_status_function_that_ignores_needs_drops_inherited_authorization(self) -> None:
        for condition in ('always()', "${{ !cancelled() }}", "failure() || needs.authorize-publish.result == 'success'",
                          'success() || inputs.force', 'Always()', '${{ !Cancelled() }}',
                          "always() && (inputs.force || needs.authorize-publish.result == 'success')",
                          "!(always() && needs.authorize-publish.result == 'success')",
                          "always() && ${{ inputs.ok }} && needs.authorize-publish.result == 'success'"):
            with self.subTest(condition=condition):
                jobs = {'authorize-publish': AUTHORIZE,
                        'publish': publisher(needs='authorize-publish', environment='publish-images', **{'if': condition})}
                self.assertEqual(problems(jobs), ['release.yml:publish: publishing job without authorization'])

    def test_a_status_function_keeps_authorization_when_the_need_must_succeed(self) -> None:
        for condition in ("${{ !cancelled() && needs.authorize-publish.result == 'success' }}",
                          "always() && (inputs.publish && (needs.authorize-publish.result == 'SUCCESS'))",
                          'success() && inputs.publish', 'SUCCESS()',
                          "(github.event_name != 'pull_request' || !github.event.pull_request.draft) && inputs.publish"):
            with self.subTest(condition=condition):
                jobs = {'authorize-publish': AUTHORIZE,
                        'publish': publisher(needs='authorize-publish', environment='publish-images', **{'if': condition})}
                self.assertEqual(problems(jobs), [])

    def test_the_authorization_job_takes_only_literal_tag_patterns(self) -> None:
        for patterns in ('*', 'v* *', '?*', '[a-z]*', '${{ inputs.tag-patterns }}'):
            with self.subTest(patterns=patterns):
                jobs = {'authorize-publish': {**AUTHORIZE, 'with': {'tag-patterns': patterns}},
                        'publish': publisher(needs='authorize-publish', environment='publish-images')}
                self.assertEqual(problems(jobs), [
                    f'release.yml:authorize-publish: tag-patterns {patterns!r} must be literal globs that start with a letter or digit'])

    def test_an_unguarded_publisher_fails_both_rules(self) -> None:
        self.assertEqual(problems({'publish': publisher()}), [
            'release.yml:publish: publishing job without authorization',
            'release.yml:publish: publishing job without environment'])

    def test_every_kind_of_publication_is_recognised(self) -> None:
        steps = [
            {'run': 'docker buildx imagetools create -t "$tag" "$source"'},
            {'run': 'docker push ghcr.io/honua-io/honua-server:x'},
            {'run': 'dotnet nuget push nupkgs/*.nupkg --source https://nuget.pkg.github.com/honua-io/index.json'},
            {'run': 'gh release create "$tag" assets/*'},
            {'run': 'python3 scripts/ci/promote-verified-image.py --candidate "$CANDIDATE"'},
            {'run': 'crane copy "$source" "$target"'},
            {'uses': 'softprops/action-gh-release@v3'},
            {'uses': 'actions/attest-build-provenance@v4', 'with': {'push-to-registry': True}},
            {'uses': 'docker/build-push-action@v7', 'with': {'outputs': 'type=image,name=ghcr.io/x,push-by-digest=true,push=true'}},
        ]
        for step in steps:
            with self.subTest(step=step):
                self.assertEqual(len(problems({'publish': {'steps': [step]}})), 2)

    def test_builds_that_publish_nothing_are_not_publishers(self) -> None:
        steps = [
            {'uses': 'docker/build-push-action@v7', 'with': {'push': False, 'load': True}},
            {'uses': 'docker/build-push-action@v7', 'with': {'push': True, 'tags': 'localhost:5000/honua:test'}},
            {'uses': 'docker/login-action@v4', 'with': {'registry': 'ghcr.io'}},
            {'uses': 'actions/attest-build-provenance@v4', 'with': {'subject-path': 'artifact.tgz'}},
            {'run': 'docker push localhost:5000/honua:test'},
            {'run': 'cp scripts/ci/gp_devkit_publication.py artifacts/'},
        ]
        self.assertEqual(problems({'build': {'steps': steps}}), [])

    def test_an_exception_excuses_only_the_rule_it_names(self) -> None:
        reason = {'release.yml:publish': {'environment': 'pending the role trust'}}
        self.assertEqual(problems({'publish': publisher()}, reason),
                         ['release.yml:publish: publishing job without authorization'])

    def test_an_exception_that_no_longer_applies_fails(self) -> None:
        jobs = {'authorize-publish': AUTHORIZE, 'publish': publisher(needs='authorize-publish', environment='publish-images')}
        reason = {'release.yml:publish': {'environment': 'pending'}, 'release.yml:gone': {'authorization': 'old'}}
        self.assertEqual(problems(jobs, reason), [
            'release.yml:publish: the environment exception no longer applies; remove it',
            'release.yml:gone: the authorization exception names no publishing job; remove it'])

    def test_an_exception_needs_a_known_rule_and_a_reason(self) -> None:
        reason = {'release.yml:publish': {'authorization': ' ', 'approval': 'x', 'environment': 'pending'}}
        found = problems({'publish': publisher()}, reason)
        self.assertIn("release.yml:publish: exception 'approval' needs a rule in ('authorization', 'environment') and a reason", found)
        self.assertIn("release.yml:publish: exception 'authorization' needs a rule in ('authorization', 'environment') and a reason", found)

    def test_this_repository_passes(self) -> None:
        self.assertEqual(check.main([]), 0)

    def test_unguarded_publishers_are_authorized_before_they_run(self) -> None:
        for name, job in (('deploy.yml', 'build-and-push'), ('nuget-publish.yml', 'publish'),
                          ('gp-release-evidence.yml', 'publish'), ('deploy-platform-images.yml', 'publish-manifests'),
                          ('control-plane-sdk-governance.yml', 'publish-release-assets'),
                          ('native-tools-publish.yml', 'publish'), ('rc-channel-tag.yml', 'move'),
                          ('gp-devkit-publish.yml', 'publish'), ('worker-gdal-image.yml', 'publish-nightly')):
            with self.subTest(workflow=name):
                jobs = yaml.safe_load((ROOT / '.github/workflows' / name).read_text())['jobs']
                self.assertEqual(jobs['authorize-publish']['uses'], check.AUTHORIZER)
                needs = jobs[job]['needs']
                self.assertIn('authorize-publish', [needs] if isinstance(needs, str) else needs)

    def test_the_authorization_job_admits_only_trunk_and_named_tags(self) -> None:
        workflow = yaml.safe_load((ROOT / '.github/workflows/publish-authorization.yml').read_text())
        job = workflow['jobs']['authorize']
        self.assertEqual(workflow['permissions'], {})
        script = job['steps'][0]['run']
        cases = [('refs/heads/trunk', '', 0), ('refs/heads/trunk', 'v*', 0), ('refs/tags/v2026.1.0', 'v*', 0),
                 ('refs/tags/honua-core-v1.2.3', 'honua-core-v* v*.*.*', 0), ('refs/tags/v1.2.3', 'honua-core-v* v*.*.*', 0),
                 ('refs/tags/v2026.1.0', '', 1), ('refs/tags/nightly-candidate-abc1234', 'v*', 1),
                 ('refs/heads/release', 'v*', 1), ('refs/heads/v1', 'v*', 1), ('refs/heads/trunk-copy', '', 1),
                 ('refs/pull/7/merge', 'v*', 1), ('refs/tags/x', '*/../v*', 1), ('refs/tags/vx/evil', 'v*', 1),
                 ('refs/heads/TRUNK', '', 1), ('refs/tags/honua-core-v1/x', 'honua-core-v*', 1)]
        for ref, patterns, expected in cases:
            with self.subTest(ref=ref, patterns=patterns):
                run = subprocess.run(['bash', '-c', script], capture_output=True, text=True, cwd=ROOT,
                                     env={**os.environ, 'GITHUB_REF': ref, 'TAG_PATTERNS': patterns,
                                          'GITHUB_WORKFLOW': 'Build & Publish Images'})
                self.assertEqual(run.returncode, expected, run.stdout + run.stderr)
                if expected:
                    self.assertIn(f'::error::Build & Publish Images publishes only from trunk', run.stdout)
                    self.assertIn(ref, run.stdout)

    def test_platform_image_pushes_refuse_any_ref_but_trunk_or_a_release_tag(self) -> None:
        # build-and-push publishes without needing the shared job (scan_only dispatches must run on
        # branches), so its first step makes the same exact-ref test whenever the run publishes.
        workflow = yaml.safe_load((ROOT / '.github/workflows/deploy-platform-images.yml').read_text())
        step = workflow['jobs']['build-and-push']['steps'][0]
        self.assertEqual(step['if'], "env.PUBLISH_IMAGES == 'true'")
        cases = [('refs/heads/trunk', 0), ('refs/tags/v2026.1.0', 0), ('refs/heads/TRUNK', 1),
                 ('refs/heads/trunk-copy', 1), ('refs/tags/vx/evil', 1), ('refs/tags/nightly-candidate-abc1234', 1),
                 ('refs/pull/7/merge', 1)]
        for ref, expected in cases:
            with self.subTest(ref=ref):
                run = subprocess.run(['bash', '-e', '-c', step['run']], capture_output=True, text=True, cwd=ROOT,
                                     env={**os.environ, 'GITHUB_REF': ref})
                self.assertEqual(run.returncode, expected, run.stdout + run.stderr)

    def test_exceptions_file_is_sorted_and_reasoned(self) -> None:
        text = (ROOT / 'scripts/ci/publish-authorization-exceptions.json').read_text()
        data = json.loads(text)
        self.assertEqual(text, json.dumps(dict(sorted(data.items())), indent=2) + '\n')


if __name__ == '__main__':
    unittest.main()
