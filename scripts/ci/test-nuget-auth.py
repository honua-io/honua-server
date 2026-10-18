#!/usr/bin/env python3
"""Secret-safe credential handoff controls; live feed proof is a separate real restore."""
import contextlib
import importlib.util
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
ACTION = ROOT / '.github/actions/setup-dotnet-ci'


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


auth = load('nuget_auth', ACTION / 'configure-nuget-auth.py')
proof = load('nuget_proof', ROOT / 'scripts/ci/prove-nuget-auth.py')
TOKEN = 'synthetic-unit-only-token'
CONFIG = '<configuration><packageSources><clear/><add key="github-honua" value="' + auth.FEED + '"/></packageSources></configuration>'


class CredentialTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.parent = Path(self.temp.name)
        self.root = self.parent / 'checkout'
        self.root.mkdir()
        self.output = self.parent / 'runner-environment'
        self.environment = {'HONUA_NUGET_USERNAME': 'unit-user', 'HONUA_NUGET_TOKEN': TOKEN,
                            'GITHUB_ENV': str(self.output)}

    def config(self, text=CONFIG, name='NuGet.config'):
        path = self.root / name
        path.write_bytes(text.encode('utf-8-sig'))
        return path

    def test_both_config_spellings_keep_exact_bytes_and_propagate_to_real_child(self):
        for name in ('NuGet.Config', 'NuGet.config'):
            with self.subTest(name=name):
                path = self.config(name=name)
                before = path.read_bytes()
                self.assertTrue(auth.configure(self.root, self.environment))
                self.assertEqual(before, path.read_bytes())
                key, value = self.output.read_text().splitlines()[-1].split('=', 1)
                self.assertEqual(key, auth.CREDENTIAL)
                self.assertEqual(value, 'Username=unit-user;Password=' + TOKEN + ';ValidAuthenticationTypes=Basic')
                # An exact mixed-case, hyphenated source variable reaches the
                # child environment. This is not private-feed authentication proof.
                subprocess.run([sys.executable, '-c', 'import os,sys; sys.exit(0 if os.environ[sys.argv[1]] == sys.argv[2] else 1)',
                                key, value], env={**os.environ, key: value}, capture_output=True, check=True)
                path.unlink()

    def test_missing_source_comment_remove_and_clear_do_not_emit_credentials(self):
        variants = ('<configuration/>', '<configuration><!-- github-honua --></configuration>',
                    CONFIG.replace('</packageSources>', '<remove key="github-honua"/></packageSources>'),
                    CONFIG.replace('</packageSources>', '<clear/></packageSources>'))
        self.assertFalse(auth.configure(self.root, {}))
        for text in variants:
            with self.subTest(text=text):
                self.config(text)
                self.assertFalse(auth.configure(self.root, {}))
                self.assertFalse(self.output.exists())

    def test_malformed_credentials_cannot_inject_runner_environment(self):
        self.config()
        for field in ('HONUA_NUGET_USERNAME', 'HONUA_NUGET_TOKEN'):
            for value in ('', 'x;Password=injected', 'x\nEXTRA=value', 'x\rEXTRA=value', 'x\0bad'):
                with self.subTest(field=field, value=value), self.assertRaises(auth.ConfigurationError):
                    auth.configure(self.root, {**self.environment, field: value})
                self.assertFalse(self.output.exists())

    def test_wrong_endpoint_and_malformed_xml_fail_without_echoing_input(self):
        for text in (CONFIG.replace(auth.FEED, 'https://unrelated.invalid/' + TOKEN),
                     '<configuration secret="' + TOKEN + '">'):
            self.config(text)
            with self.assertRaises(auth.ConfigurationError) as error:
                auth.configure(self.root, self.environment)
            self.assertNotIn(TOKEN, str(error.exception))
            self.assertFalse(self.output.exists())

    def test_runner_environment_cannot_be_written_into_checkout(self):
        self.config()
        for output in ('', str(self.root / 'artifact.txt')):
            with self.subTest(output=output), self.assertRaises(auth.ConfigurationError):
                auth.configure(self.root, {**self.environment, 'GITHUB_ENV': output})
        self.assertEqual(list(self.root.iterdir()), [self.root / 'NuGet.config'])

    def test_cli_success_and_failure_never_print_secret_or_configuration(self):
        self.config()
        environment = {**os.environ, **self.environment}
        for token in (TOKEN, TOKEN + ';malformed'):
            result = subprocess.run([sys.executable, str(ACTION / 'configure-nuget-auth.py')], cwd=self.root,
                                    env={**environment, 'HONUA_NUGET_TOKEN': token}, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0 if token == TOKEN else 1)
            self.assertNotIn(TOKEN, result.stdout + result.stderr)
            self.assertNotIn('<configuration>', result.stdout + result.stderr)

    def test_ordinary_prs_do_not_cold_restore_but_auth_changes_and_dispatch_do(self):
        for event, paths, expected in (
                ('pull_request', ['src/Honua.Server/Program.cs'], False),
                ('pull_request', ['docs/install.md'], False),
                ('pull_request', ['.github/actions/setup-dotnet-ci/action.yml'], True),
                ('pull_request', ['.github/actions/setup-dotnet-ci/configure-nuget-auth.py'], True),
                ('pull_request', ['scripts/ci/prove-nuget-auth.py'], True),
                ('pull_request', ['scripts/ci/test-nuget-auth.py'], True),
                ('pull_request', ['NuGet.config'], True),
                ('pull_request', None, True), ('workflow_dispatch', [], True)):
            with self.subTest(event=event, paths=paths):
                self.assertEqual(proof.selected(event, paths), expected)

    def test_actual_restore_uses_fresh_cache_and_only_explicit_credential(self):
        cache = self.parent / 'isolated'
        cache.mkdir()
        for credential in (None, 'Username=unit;Password=' + TOKEN):
            with patch.dict(os.environ, {proof.KEY: 'stale-credential'}, clear=False), \
                    patch.object(proof.subprocess, 'run') as run:
                proof.restore(cache / 'Probe.csproj', self.root / 'NuGet.config', cache, credential)
            call = run.call_args
            self.assertIn('--no-cache', call.args[0])
            self.assertIn('--force-evaluate', call.args[0])
            environment = call.kwargs['env']
            self.assertEqual(environment.get(proof.KEY), credential)
            self.assertEqual(environment['NUGET_PACKAGES'], str(cache / 'packages'))
            self.assertEqual(environment['NUGET_HTTP_CACHE_PATH'], str(cache / 'http'))
            self.assertEqual(environment['NUGET_PLUGINS_CACHE_PATH'], str(cache / 'plugins'))
            self.assertTrue(call.kwargs['capture_output'])
            self.assertEqual(call.kwargs['timeout'], 150)

    def test_proof_diagnostics_do_not_echo_raw_restore_errors(self):
        for error in (proof.ProofError('Authenticated cold restore failed; check package access and feed availability.'),
                      OSError(TOKEN), ValueError(TOKEN)):
            stream = io.StringIO()
            with patch.object(proof, 'changed_paths', return_value=None), \
                    patch.object(proof, 'prove', side_effect=error), contextlib.redirect_stderr(stream):
                self.assertEqual(proof.main(), 1)
            self.assertNotIn(TOKEN, stream.getvalue())

    def test_action_and_workflow_keep_real_proof_and_credential_contract(self):
        action = (ACTION / 'action.yml').read_text()
        self.assertNotIn('dotnet nuget update source', action)
        self.assertNotIn('--store-password-in-clear-text', action)
        self.assertIn('configure-nuget-auth.py', action)
        self.assertIn('HONUA_NUGET_TOKEN: ${{ github.token }}', action)
        workflow = (ROOT / '.github/workflows/pr-gate.yml').read_text()
        self.assertIn('run: python3 scripts/ci/prove-nuget-auth.py', workflow)
        self.assertNotIn('prove-nuget-auth.py || true', workflow)


if __name__ == '__main__':
    unittest.main()
