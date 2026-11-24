#!/usr/bin/env python3
"""Offline regression tests for container release identity."""
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = Path(__file__).with_name('container-version.py')

HANDOFF_REPOSITORY = 'honua-io/honua-server'
HANDOFF_GH_STUB = r'''gh() {
          case "$*" in
            "api repos/${GITHUB_REPOSITORY}/git/ref/tags/${tag} --jq .object.sha")
              printf '%s\n' "$CANDIDATE_SHA" ;;
            "api --paginate repos/${GITHUB_REPOSITORY}/actions/workflows/nightly-container-build.yml/runs?head_sha=${CANDIDATE_SHA}&per_page=100")
              printf '%s\n' "$RUN_PAGES" ;;
            "workflow run nightly-container-build.yml --repo ${GITHUB_REPOSITORY} --ref ${tag}"*)
              printf 'DISPATCH:'
              printf '<%s>' "$@"
              printf '\n' ;;
            *)
              printf 'Unexpected gh call: %s\n' "$*" >&2
              return 1 ;;
          esac
        }
'''

class ContainerVersionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location('container_version', SCRIPT)
        cls.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.module)

    def test_explicit_candidate_and_stable_versions(self):
        for version in ('2026.1.0-rc.2', '2026.1.0'):
            self.assertEqual(self.module.derive(version, 'a' * 40),
                             (version, version + '+aaaaaaa'))

    def test_nightly_is_deterministic_and_uses_selected_commit(self):
        self.assertEqual(self.module.derive('', 'abcdef0123456789' + '0' * 24),
                         ('2026.1.0-nightly.gabcdef0', '2026.1.0-nightly.gabcdef0+abcdef0'))

    def test_numeric_sha_with_leading_zero_produces_valid_semver(self):
        version, informational = self.module.derive('', '0123456' + 'a' * 33)
        self.assertEqual(version, '2026.1.0-nightly.g0123456')
        self.assertIsNotNone(self.module.VERSION.fullmatch(version))
        self.assertEqual(informational, version + '+0123456')

    def test_rejects_malformed_or_unsafe_release_versions(self):
        for version in ('v2026.1.0', '2026.1', '2026.01.0', '2026.1.0-rc.02',
                        '2026.1.0+metadata', '2026.1.0\nother=value', '-p:Bad=true'):
            with self.subTest(version=version), self.assertRaises(ValueError):
                self.module.derive(version, 'a' * 40)

    def test_rejects_invalid_revision(self):
        for sha in ('', 'abcdef0', 'A' * 40, 'z' * 40):
            with self.subTest(sha=sha), self.assertRaises(ValueError):
                self.module.derive('', sha)

    def test_workflow_forwards_version_to_candidate_handoff_and_builds(self):
        workflow = (ROOT / '.github/workflows/nightly-container-build.yml').read_text()
        self.assertIn('release_version:', workflow)
        self.assertIn('-f release_version="${RELEASE_VERSION}"', workflow)
        self.assertEqual(workflow.count('VERSION=${{ needs.select-candidate.outputs.version }}'), 3)
        self.assertEqual(workflow.count('org.opencontainers.image.version=${{ needs.select-candidate.outputs.version }}'), 6)
        self.assertEqual(workflow.count('index:org.opencontainers.image.version=${IMAGE_VERSION}'), 3)

    def test_customer_quickstart_is_packaged_with_the_image_platform_version(self):
        workflow = (ROOT / '.github/workflows/nightly-container-build.yml').read_text()
        step = workflow.split('- name: Package immutable customer quickstart', 1)[1].split('- uses:', 1)[0]
        self.assertIn('PLATFORM_VERSION: ${{ needs.select-candidate.outputs.version }}', step)
        self.assertIn('--version "$PLATFORM_VERSION"', step)
        self.assertNotIn('nightly-${CANDIDATE_SHA7}', step)

    def handoff_script(self):
        workflow = (ROOT / '.github/workflows/nightly-container-build.yml').read_text()
        step = workflow.split('      - name: Tag the selected sha and re-dispatch this workflow at it\n', 1)[1]
        config, script = step.split('        run: |\n', 1)
        self.assertIn('          RELEASE_VERSION: ${{ inputs.release_version }}\n', config)
        self.assertIn('          ECS_ONLY: ${{ inputs.ecs_only }}\n', config)
        return textwrap.dedent(re.split(r'\n(?= {0,9}\S)', script, maxsplit=1)[0])

    def test_handoff_skips_duplicate_publications_and_preserves_dispatch_inputs(self):
        handoff = self.handoff_script()
        tag = 'nightly-candidate-aaaaaaa'
        full = {
            'status': 'queued', 'event': 'workflow_dispatch', 'head_branch': tag,
            'head_repository': {'full_name': HANDOFF_REPOSITORY},
            'display_title': f'Nightly Container Build · refs/tags/{tag}',
        }
        ecs = dict(full, status='in_progress', display_title=f'ECS-only candidate · refs/tags/{tag}')
        scheduled = dict(full, event='schedule', head_branch='trunk',
                         display_title='Nightly Container Build · refs/heads/trunk')
        legacy = dict(full, status='in_progress', display_title='Nightly Container Build')
        unrelated = [
            dict(full, status='completed'),
            dict(full, display_title=f'Nightly Container Build · refs/heads/{tag}'),
            dict(full, head_repository={'full_name': 'fork/honua-server'}),
        ]
        version = '2026.1.0-rc.2'
        dispatch = f'DISPATCH:<workflow><run><nightly-container-build.yml><--repo><{HANDOFF_REPOSITORY}><--ref><{tag}>'
        cases = (
            ('legacy inputs', [unrelated], '', 'false', dispatch),
            ('release version', [[]], version, 'false', f'{dispatch}<-f><release_version={version}>'),
            ('ECS only', [[]], '', 'true', f'{dispatch}<-f><ecs_only=true>'),
            ('versioned ECS', [[]], version, 'true', f'{dispatch}<-f><release_version={version}><-f><ecs_only=true>'),
            ('queued tagged build on first page', [[full], unrelated], '', 'false', None),
            ('queued tagged build on second page', [unrelated, [full]], '', 'false', None),
            ('scheduled trunk build', [[scheduled]], '', '', None),
            ('active legacy build', [[legacy]], '', 'false', None),
            ('active ECS cannot cover full publication', [[ecs]], '', 'false', 'error'),
            ('active build cannot accept a release version', [[full]], version, 'false', 'error'),
            ('active build cannot accept ECS input', [[full]], '', 'true', 'error'),
        )
        for name, *case in cases:
            with self.subTest(name=name):
                self.assert_handoff(handoff, case)

    def assert_handoff(self, handoff, case):
        pages, release, ecs_only, expected = case
        with tempfile.TemporaryDirectory() as temp:
            result = subprocess.run(
                ['bash', '-c', HANDOFF_GH_STUB + handoff], text=True, capture_output=True,
                env={'PATH': os.environ.get('PATH', os.defpath),
                     'RELEASE_VERSION': release, 'ECS_ONLY': ecs_only, 'PARTIAL_PUBLICATION': '0',
                     'RUN_PAGES': '\n'.join(json.dumps({'workflow_runs': page}) for page in pages),
                     'RUNNER_TEMP': temp,
                     'GITHUB_STEP_SUMMARY': str(Path(temp) / 'summary.md'),
                     'GITHUB_REPOSITORY': HANDOFF_REPOSITORY, 'HANDOFF_TOKEN': 'test',
                     'CANDIDATE_SHA': 'a' * 40, 'GITHUB_SHA': 'b' * 40})
        output = result.stdout + result.stderr
        self.assertEqual(result.returncode, 1 if expected == 'error' else 0, output)
        dispatches = [line for line in result.stdout.splitlines() if line.startswith('DISPATCH:')]
        if expected == 'error':
            self.assertIn('is already active', output)
            self.assertEqual(dispatches, [])
        elif expected is None:
            self.assertIn('is already queued or running', output)
            self.assertEqual(dispatches, [])
        else:
            self.assertEqual(dispatches, [expected])

    def test_dockerfiles_stamp_publish_and_runtime_label(self):
        for name in ('Dockerfile', 'docker/Dockerfile.aot', 'docker/Dockerfile.lambda',
                     'docker/Dockerfile.lambda.aot'):
            with self.subTest(name=name):
                dockerfile = (ROOT / name).read_text()
                self.assertIn('ARG VERSION=', dockerfile)
                self.assertIn('-p:Version=', dockerfile)
                self.assertIn('-p:InformationalVersion=', dockerfile)
                self.assertIn('org.opencontainers.image.version="${VERSION}"', dockerfile)

    def test_publish_flags_expand_as_individual_arguments_and_preserve_local_default(self):
        for name in ('Dockerfile', 'docker/Dockerfile.aot', 'docker/Dockerfile.lambda',
                     'docker/Dockerfile.lambda.aot'):
            text = (ROOT / name).read_text()
            flags = text.split('dotnet publish src/Honua.Server/Honua.Server.csproj', 1)[1].split('--configuration', 1)[0]
            command = 'dotnet() { printf "%s\\n" "$@"; }; dotnet' + flags
            for version in ('', '2026.1.0-rc.2', '2026.1.0'):
                with self.subTest(name=name, version=version):
                    result = subprocess.run(['sh', '-c', command], check=True, text=True,
                                            capture_output=True,
                                            env={'VERSION': version, 'HONUA_GIT_SHA': 'a' * 40})
                    arguments = result.stdout.splitlines()
                    version_arguments = (['-p:Version=' + version,
                        '-p:InformationalVersion=' + version + '+aaaaaaa',
                        '-p:IncludeSourceRevisionInInformationalVersion=false'] if version else [])
                    self.assertEqual(arguments, version_arguments or [''])


if __name__ == '__main__':
    unittest.main()
