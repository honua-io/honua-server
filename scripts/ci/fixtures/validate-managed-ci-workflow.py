#!/usr/bin/env python3
"""Cheap structural regression checks for the managed full-CI handoff."""
from pathlib import Path
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


if __name__ == '__main__':
    unittest.main()
