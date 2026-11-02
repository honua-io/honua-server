#!/usr/bin/env python3
"""Offline regression tests for container release identity."""
import importlib.util
from pathlib import Path
import unittest
import subprocess

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = Path(__file__).with_name('container-version.py')


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
        self.assertEqual(workflow.count('VERSION=${{ needs.select-candidate.outputs.version }}'), 4)
        self.assertEqual(workflow.count('org.opencontainers.image.version=${{ needs.select-candidate.outputs.version }}'), 8)
        self.assertEqual(workflow.count('index:org.opencontainers.image.version=${IMAGE_VERSION}'), 3)

    def test_handoff_handles_existing_images_and_older_workflow_inputs(self):
        workflow = (ROOT / '.github/workflows/nightly-container-build.yml').read_text()
        handoff = workflow.split('          # One build per candidate:', 1)[1].split('          {\n', 1)[0]
        # Exercise the real handoff shell with only GitHub reads/writes replaced.
        mock = r'''gh() {
          if [ "$1" = run ]; then
            case "$*" in *headSha*) echo "$IMAGED" ;; *) echo "$ACTIVE" ;; esac
          else
            printf 'DISPATCH:'
            printf '<%s>' "$@"
            printf '\n'
          fi
        }
'''
        for release, active, imaged, expected_dispatch, expected_error in (
            ('', '0', '0', True, False),
            ('', '1', '0', False, False),
            ('2026.1.0-rc.2', '1', '0', False, True),
        ):
            with self.subTest(release=release, active=active, imaged=imaged):
                result = subprocess.run(['bash', '-c', 'set -euo pipefail\n' + mock +
                    '# One build per candidate:' + handoff], text=True, capture_output=True,
                    env={'RELEASE_VERSION': release, 'ACTIVE': active, 'IMAGED': imaged,
                         'GITHUB_REPOSITORY': 'honua-io/honua-server', 'HANDOFF_TOKEN': 'test',
                         'CANDIDATE_SHA': 'a' * 40, 'GITHUB_SHA': 'b' * 40,
                         'tag': 'nightly-candidate-aaaaaaa'})
                self.assertEqual(result.returncode != 0, expected_error, result.stderr)
                self.assertEqual('DISPATCH:' in result.stdout, expected_dispatch, result.stdout)
                if expected_dispatch:
                    self.assertEqual('<release_version=' in result.stdout, bool(release))
                    if release:
                        self.assertIn(f'<release_version={release}>', result.stdout)

    def test_dockerfiles_stamp_publish_and_runtime_label(self):
        for name in ('Dockerfile', 'docker/Dockerfile.aot', 'docker/Dockerfile.lambda',
                     'docker/Dockerfile.lambda.aot', 'docker/Dockerfile.functions.aot'):
            with self.subTest(name=name):
                dockerfile = (ROOT / name).read_text()
                self.assertIn('ARG VERSION=', dockerfile)
                self.assertIn('-p:Version=', dockerfile)
                self.assertIn('-p:InformationalVersion=', dockerfile)
                self.assertIn('org.opencontainers.image.version="${VERSION}"', dockerfile)

    def test_publish_flags_expand_as_individual_arguments_and_preserve_local_default(self):
        for name in ('Dockerfile', 'docker/Dockerfile.aot', 'docker/Dockerfile.lambda',
                     'docker/Dockerfile.lambda.aot', 'docker/Dockerfile.functions.aot'):
            text = (ROOT / name).read_text()
            flags = text.split('dotnet publish src/Honua.Server/Honua.Server.csproj', 1)[1].split('--configuration', 1)[0]
            command = 'dotnet() { printf "%s\\n" "$@"; }; dotnet' + flags
            for version in ('', '2026.1.0-rc.2', '2026.1.0'):
                with self.subTest(name=name, version=version):
                    result = subprocess.run(['sh', '-c', command], check=True, text=True,
                                            capture_output=True,
                                            env={'VERSION': version, 'HONUA_GIT_SHA': 'a' * 40})
                    arguments = result.stdout.splitlines()
                    if version:
                        self.assertEqual(arguments, ['-p:Version=' + version,
                            '-p:InformationalVersion=' + version + '+aaaaaaa',
                            '-p:IncludeSourceRevisionInInformationalVersion=false'])
                    else:
                        self.assertEqual(arguments, [''])


if __name__ == '__main__':
    unittest.main()
