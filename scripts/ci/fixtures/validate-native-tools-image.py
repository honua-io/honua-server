#!/usr/bin/env python3
"""Offline identity/provenance/fallback tests: never contact Docker or GitHub."""
import importlib.util
import json
from pathlib import Path
import shutil
import sys
import time
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('native_tools', ROOT / 'scripts/ci/native-tools-image.py')
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)
IMAGE_ID = 'sha256:' + '1' * 64
DIGEST = 'sha256:' + '2' * 64
SOURCE = '3' * 40


class NativeToolsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in [native.DOCKERFILE, '.dockerignore', 'scripts/ci/native-tools-image.py', native.WORKFLOW]:
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / name, target)
        self.calls = []
        self.expected = native.identity(self.root)
        self.failure = None
        self.label = self.expected['identity']
        self.image_platform = 'amd64'

    def fake(self, argv, root, timeout):
        self.assertEqual(root, self.root)
        self.assertGreater(timeout, 0)
        self.calls.append((argv, timeout))
        if argv[:4] == ['docker', 'buildx', 'imagetools', 'inspect']:
            if self.failure == 'registry':
                raise RuntimeError('OFFLINE_SECRET_MUST_NOT_APPEAR')
            return json.dumps({'digest': DIGEST})
        if argv[:2] == ['docker', 'pull']:
            return ''
        if argv[:3] == ['docker', 'image', 'inspect']:
            architecture = self.image_platform
            if argv[-1].startswith('honua-pdal-proof-'):
                builds = [call for call, _ in self.calls if call[:3] == ['docker', 'buildx', 'build']]
                architecture = builds[-1][builds[-1].index('--platform') + 1].split('/')[1]
            return json.dumps([{'Id': IMAGE_ID, 'Os': 'linux', 'Architecture': architecture,
                                'Config': {'Labels': {native.LABEL: self.label,
                                                     'org.opencontainers.image.revision': SOURCE}}}])
        if argv[:3] == ['gh', 'attestation', 'verify']:
            if self.failure == 'attestation':
                raise RuntimeError('Wrong publisher signature/ref/source/issuer')
            self.assertIn(f'oci://{native.IMAGE}@{DIGEST}', argv)
            self.assertIn('--bundle-from-oci', argv)
            for flag, value in [('--repo', 'honua-io/honua-server'),
                                ('--source-ref', 'refs/heads/trunk'),
                                ('--source-digest', SOURCE),
                                ('--cert-oidc-issuer', 'https://token.actions.githubusercontent.com'),
                                ('--signer-workflow', f'honua-io/honua-server/{native.WORKFLOW}'),
                                ('--cert-identity', f'https://github.com/honua-io/honua-server/{native.WORKFLOW}@refs/heads/trunk')]:
                self.assertEqual(argv[argv.index(flag) + 1], value)
            return 'VERIFIED'
        if argv[:3] == ['docker', 'buildx', 'build']:
            self.assertEqual(timeout, 1800)
            self.assertIn('--load', argv)
            self.assertNotIn('--push', argv)
            self.assertEqual(argv[argv.index('--target') + 1], 'native-tools')
            return ''
        self.fail(f'Unexpected command {argv}')

    def built(self):
        return any(argv[:3] == ['docker', 'buildx', 'build'] for argv, _ in self.calls)

    def test_exact_trusted_digest_reused_without_build(self):
        result = native.resolve(self.root, run=self.fake)
        self.assertEqual(result['mode'], 'reused')
        self.assertEqual(result['image'], f'{native.IMAGE}@{DIGEST}')
        self.assertEqual(result['image_id'], IMAGE_ID)
        self.assertFalse(self.built())
        pull = next(argv for argv, _ in self.calls if argv[:2] == ['docker', 'pull'])
        self.assertEqual(pull[-1], result['image'])
        self.assertTrue(self.expected['inputs']['pinned_bases'])

    def test_full_dockerfile_globals_platform_args_and_policy_change_identity(self):
        original = native.identity(self.root)['identity']
        for override in ['PDAL_VERSION=2.10.3', 'DOTNET_RUNTIME_VERSION=10.0.12']:
            self.assertNotEqual(original, native.identity(self.root, overrides=[override])['identity'])
        self.assertNotEqual(original, native.identity(self.root, platform='linux/arm64')['identity'])
        for name in [native.DOCKERFILE, '.dockerignore', native.WORKFLOW, 'scripts/ci/native-tools-image.py']:
            file = self.root / name
            previous = file.read_bytes()
            file.write_bytes(previous + b'\n# changed\n')
            self.assertNotEqual(original, native.identity(self.root)['identity'])
            file.write_bytes(previous)

    def test_reachable_copy_inputs_are_hashed(self):
        dockerfile = self.root / native.DOCKERFILE
        dockerfile.write_text(dockerfile.read_text().replace('FROM native-tools AS runtime',
            'COPY native-input.txt /opt/native-input.txt\nFROM native-tools AS runtime'))
        source = self.root / 'native-input.txt'
        source.write_text('one')
        original = native.identity(self.root)['identity']
        source.write_text('two')
        self.assertNotEqual(original, native.identity(self.root)['identity'])
        source.unlink()
        with self.assertRaises(native.UnsupportedInput):
            native.identity(self.root)

    def test_unknown_external_inputs_fail_closed(self):
        dockerfile = self.root / native.DOCKERFILE
        dockerfile.write_text(dockerfile.read_text().replace('FROM native-tools AS runtime',
            'ADD https://example.invalid/mutable.tar /opt/\nFROM native-tools AS runtime'))
        with self.assertRaises(native.UnsupportedInput):
            native.identity(self.root)
        result = native.resolve(self.root, run=self.fake)
        self.assertEqual(result['reason'], 'unsupported-inputs')
        self.assertTrue(self.built())
        self.assertFalse(any(argv[0] == 'gh' for argv, _ in self.calls))

    def test_missing_offline_untrusted_mismatch_and_platform_all_build(self):
        for failure in ['registry', 'attestation', 'identity', 'platform']:
            with self.subTest(failure=failure):
                self.calls = []
                self.failure = failure
                self.label = 'mismatch' if failure == 'identity' else self.expected['identity']
                self.image_platform = 'arm64' if failure == 'platform' else 'amd64'
                result = native.resolve(self.root, run=self.fake)
                self.assertEqual(result['mode'], 'fallback')
                self.assertEqual(result['image_id'], IMAGE_ID)
                self.assertTrue(self.built())
                self.assertNotIn('SECRET', json.dumps(result))

    def test_rollback_and_other_platform_never_lookup(self):
        for options in [{'reuse': False}, {'platform': 'linux/arm64'}]:
            self.calls = []
            result = native.resolve(self.root, run=self.fake, **options)
            self.assertEqual(result['mode'], 'fallback')
            self.assertTrue(self.built())
            self.assertFalse(any(argv[:2] == ['docker', 'pull'] or argv[0] == 'gh' for argv, _ in self.calls))

    def test_lookup_budget_caps_each_request_and_preserves_build_budget(self):
        clock = [0]
        def request(argv, root, timeout):
            self.assertLessEqual(timeout, 120)
            result = self.fake(argv, root, timeout)
            clock[0] += 121
            return result
        def bounded(argv, root, timeout):
            if argv[:3] == ['docker', 'buildx', 'build'] or argv[:3] == ['docker', 'image', 'inspect']:
                return self.fake(argv, root, timeout)
            return request(argv, root, timeout)
        result = native.resolve(self.root, run=bounded, now=lambda: clock[0])
        self.assertEqual(result['mode'], 'fallback')
        self.assertTrue(self.built())

    def test_publisher_admission_namespace_and_proofs_are_preserved(self):
        workflow = (ROOT / native.WORKFLOW).read_text()
        self.assertNotIn('pull_request:', workflow)
        self.assertIn("github.ref == 'refs/heads/trunk'", workflow)
        self.assertIn("github.repository == 'honua-io/honua-server'", workflow)
        self.assertIn('id-token: write', workflow)
        self.assertIn('subject-digest: ${{ steps.image.outputs.digest }}', workflow)
        self.assertIn('target: native-tools', workflow)
        self.assertIn(native.CACHE, workflow)
        self.assertNotIn('cache-to: type=registry,ref=' + native.LEGACY_CACHE, workflow)
        for name in ['pr-gate.yml', 'ci.yml']:
            consumer = (ROOT / '.github/workflows' / name).read_text()
            self.assertIn('native-tools-image.py resolve --output tests/TestResults/native-tools-proof.json', consumer)
            if name == 'pr-gate.yml':
                self.assertIn('Category=PdalExecutionProof', consumer)
                upload = consumer.split('- name: Upload point-cloud execution proof', 1)[1].split('- name:', 1)[0]
                self.assertIn('tests/TestResults/native-tools-proof.json', upload)
                self.assertIn('tests/TestResults/pdal-execution-proof.trx', upload)
            else:
                self.assertIn('name: Run Worker GDAL tests', consumer)
                main = consumer.split('- name: Run Worker GDAL tests', 1)[1].split('- name:', 1)[0]
                self.assertNotIn('--filter', main)
                self.assertIn('Honua.Worker.Gdal.Tests.csproj', main)
        self.assertIn('aot-build:', (ROOT / '.github/workflows/ci.yml').read_text())

    def test_unquiescent_lookup_never_starts_fallback(self):
        def stopped(argv, root, timeout):
            raise native.UnsafeProcessGroup('owned group remains alive')
        with self.assertRaises(native.UnsafeProcessGroup):
            native.resolve(self.root, run=stopped)
        self.assertFalse(self.built())

    def test_timeout_reaps_a_term_ignoring_child_even_when_its_pipes_close(self):
        ready, late = self.root / 'ready', self.root / 'late'
        script = '(trap "" TERM; printf ready > "$1"; sleep 5; printf unsafe > "$2") >/dev/null 2>&1 & wait'
        with self.assertRaises(TimeoutError):
            native.command(['bash', '-c', script, '--', str(ready), str(late)], self.root, 1)
        self.assertEqual(ready.read_text(), 'ready')
        time.sleep(2.2)
        self.assertFalse(late.exists(), 'lookup child must stop before fallback can begin')


if __name__ == '__main__':
    unittest.main()
