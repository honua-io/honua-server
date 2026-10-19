#!/usr/bin/env python3
"""Cheap structural regression checks for the managed full-CI handoff."""
from pathlib import Path
import os
import re
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = (ROOT / '.github/workflows/ci.yml').read_text()


def job(name):
    return re.split(r'\n  [a-z][a-z0-9-]*:\n',
                    WORKFLOW.split(f'  {name}:\n', 1)[1], maxsplit=1)[0]


class ManagedWorkflowTests(unittest.TestCase):
    def test_producer_preserves_affected_union_and_exact_build_properties(self):
        producer = job('build')
        self.assertIn('needs: [changes, targeted-shards]', producer)
        self.assertIn('--base-filter "${RUNNER_TEMP}/affected-base.slnf"', producer)
        self.assertIn('--matrix-file "${RUNNER_TEMP}/managed-matrix.json"', producer)
        self.assertIn('target=Honua.sln', producer)
        self.assertIn('if [[ "${AFFECTED_MODE}" == affected ]]', producer)
        for step in ['Restore producer build graph', 'Build (warnings as errors)']:
            section = producer.split(f'- name: {step}\n', 1)[1].split('\n      - name:', 1)[0]
            self.assertIn('BUILD_TARGET:', section)
            for flag in ['TreatWarningsAsErrors=true', 'RunAnalyzers=true', 'UseSharedCompilation=false']:
                self.assertIn(flag, section)
        self.assertIn('dotnet-restore-retry.sh \"${BUILD_TARGET}\" -p:Configuration=Release', producer)
        self.assertLess(producer.index('Build (warnings as errors)'), producer.index('mode: publish'))
        self.assertNotIn('dotnet test ', producer)

    def test_foundation_source_follows_regeneration_and_no_full_restore(self):
        family = job('dotnet-foundation-family-tests')
        self.assertIn('needs: [changes, build]', family)
        self.assertIn('managed-test-build.py fallback', family)
        self.assertNotIn('dotnet-restore-retry.sh Honua.sln', family)
        self.assertLess(family.index('Regenerate foundation projections'), family.index('Run source architecture'))
        self.assertEqual(WORKFLOW.count('dotnet test tests/dotnet/Honua.Architecture.SourceTests/'), 1)
        self.assertIn('! -f tests/TestResults/source-architecture-tests.trx', job('dotnet-foundation-tests'))

    def test_shard_managed_first_cache_compatibility_and_missing_only(self):
        shard = job('server-tests')
        self.assertIn('needs: [changes, targeted-shards, build]', shard)
        self.assertLess(shard.index('Consume managed shard'), shard.index('Plan shard-local exact-head cache'))
        self.assertIn("if: steps.managed-status.outputs.restored != 'true' && steps.shard-cache-materialize.outputs.restored != 'true'", shard)
        self.assertIn('managed-test-build.py fallback --projects-file', shard)
        self.assertIn('actions/cache/restore@v6', shard)
        self.assertIn('matrix.advisory != true', shard)
        self.assertIn("continue-on-error: ${{ matrix.advisory == true }}", shard)

    def test_package_prerequisites_precede_both_materializers(self):
        for name, lane in [('dotnet-foundation-family-tests', 'foundation'), ('server-tests', 'shard')]:
            with self.subTest(lane=lane):
                consumer = job(name)
                restore = f'Restore package prerequisites before {lane} materialization'
                self.assertLess(consumer.index(restore), consumer.index(f'Consume managed {lane} outputs'))
                section = consumer.split(f'- name: {restore}\n', 1)[1].split('\n      - name:', 1)[0]
                self.assertNotIn('        if:', section, 'Prerequisites cannot depend on successful materialization')
                self.assertNotIn('continue-on-error:', section)
                self.assertIn('timeout-minutes: 10', section)
                self.assertIn('managed-test-build.py fallback', section)
                for option in ['--projects-file', '--restored-file']:
                    self.assertIn(f'{option} "${{RUNNER_TEMP}}/{lane}-projects.json"', section)
                if lane == 'shard':
                    self.assertLess(consumer.index(restore), consumer.index('Verify and unpack exact-head shard cache'))

    def test_full_format_is_independent_and_required(self):
        formatter = job('format')
        self.assertIn('needs: changes', formatter)
        self.assertIn('dotnet-restore-retry.sh Honua.sln', formatter)
        self.assertIn('dotnet format Honua.sln --verify-no-changes --no-restore', formatter)
        self.assertIn('build, format, test-all', job('ci-gate'))

    def test_changed_multiline_shell_blocks_parse(self):
        # Expressions are replaced only for bash syntax checking; no commands run.
        for name in ['build', 'dotnet-foundation-family-tests', 'server-tests']:
            for match in re.finditer(r'        run: \|\n((?:          .*\n|\n)+)', job(name)):
                shell = '\n'.join(line[10:] for line in match[1].splitlines())
                shell = re.sub(r'\$\{\{.*?\}\}', 'fixture', shell)
                subprocess.run(['bash', '-n'], input=shell, text=True, check=True)

    @staticmethod
    def gate_shell(name):
        section = job(name).split('- name: Check required', 1)[1]
        match = re.search(r'        run: \|\n((?:          .*\n|\n)+)', section)
        shell = '\n'.join(line[10:] for line in match[1].splitlines())
        # Existing aggregate failure/cancellation checks are separate invariants.
        # This fixture varies only the newly added explicit selected-lane guards.
        return re.sub(r'\$\{\{.*?\}\}', 'false', shell)

    @staticmethod
    def execute_guard(shell, **values):
        return subprocess.run(['bash', '-e', '-c', shell],
                              env=dict(os.environ, **values), capture_output=True).returncode

    def test_ci_gate_selected_skips_fail_and_docs_dependabot_skips_remain_valid(self):
        shell = self.gate_shell('ci-gate')
        base = dict(REQUIRE_MANAGED_BUILD='true', REQUIRE_FORMAT='true',
                    BUILD_RESULT='success', FORMAT_RESULT='success')
        self.assertEqual(0, self.execute_guard(shell, **base))
        for lane in ['BUILD_RESULT', 'FORMAT_RESULT']:
            for result in ['skipped', 'cancelled', 'failure']:
                with self.subTest(lane=lane, result=result):
                    self.assertNotEqual(0, self.execute_guard(shell, **(base | {lane: result})))
        # Docs-only/dependabot selection deliberately requires neither lane.
        self.assertEqual(0, self.execute_guard(shell, REQUIRE_MANAGED_BUILD='false',
            REQUIRE_FORMAT='false', BUILD_RESULT='skipped', FORMAT_RESULT='skipped'))
        # Integration-only inputs still need the managed producer.
        self.assertNotEqual(0, self.execute_guard(shell, REQUIRE_MANAGED_BUILD='true',
            REQUIRE_FORMAT='false', BUILD_RESULT='skipped', FORMAT_RESULT='skipped'))
        # A mutation deleting the explicit producer requirement would incorrectly
        # admit this selected-but-skipped case (caught by the assertion above).
        mutant = shell.replace('"${REQUIRE_MANAGED_BUILD}" == true', '"${REQUIRE_MANAGED_BUILD}" == false')
        self.assertEqual(0, self.execute_guard(mutant, **(base | {'BUILD_RESULT':'skipped'})))

    def test_test_all_requires_each_selected_integration_lane(self):
        shell = self.gate_shell('test-all')
        base = dict(REQUIRE_TEST_LANES='true', FOUNDATION_MATRIX_RESULT='success',
                    FOUNDATION_RESULT='success', SERVER_TEST_RESULT='success')
        self.assertEqual(0, self.execute_guard(shell, **base))
        for lane in ['FOUNDATION_MATRIX_RESULT', 'FOUNDATION_RESULT', 'SERVER_TEST_RESULT']:
            for result in ['skipped', 'cancelled', 'failure']:
                with self.subTest(lane=lane, result=result):
                    self.assertNotEqual(0, self.execute_guard(shell, **(base | {lane:result})))
        self.assertEqual(0, self.execute_guard(shell, REQUIRE_TEST_LANES='false',
            FOUNDATION_MATRIX_RESULT='skipped', FOUNDATION_RESULT='skipped', SERVER_TEST_RESULT='skipped'))
        self.assertIn('      - changes\n', job('test-all'))
        mutant = shell.replace('"${REQUIRE_TEST_LANES}" == true', '"${REQUIRE_TEST_LANES}" == false')
        self.assertEqual(0, self.execute_guard(mutant, **(base | {'SERVER_TEST_RESULT':'skipped'})))
        # Existing merge-group/dependabot job exclusions stay authoritative.
        self.assertIn("github.event_name != 'merge_group'", job('test-all'))
        self.assertIn("github.event.pull_request.user.login != 'dependabot[bot]'", job('test-all'))


if __name__ == '__main__':
    unittest.main()
