#!/usr/bin/env python3
"""Pin required PR producer/consumer semantics without .NET or GitHub access."""
import os
import importlib.util
import tempfile
from pathlib import Path
import re
import subprocess
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[3]
TEXT = (ROOT / '.github/workflows/pr-gate.yml').read_text()


def job(name):
    return re.search(r'^  '+re.escape(name)+r':\n(.*?)(?=^  [\w-]+:|\Z)', TEXT, re.M | re.S).group(1)


class PrManagedBuildTests(unittest.TestCase):
    def test_required_results_fail_closed(self):
        required = job('required')
        self.assertIn('needs: [pr-gate, pr-gate-tests, format, ci-router-validation]', required)
        self.assertIn('if: always()', required)
        script = re.search(r'        run: \|\n(.*?)(?=\n      #|\n      -|\Z)', required, re.S).group(1)
        script = textwrap.dedent(script)
        keys = ['BUILD_AND_TEST_RESULT', 'TEST_RESULT', 'FORMAT_RESULT', 'CI_ROUTER_RESULT']
        for key, dependency in zip(keys, ['pr-gate','pr-gate-tests','format','ci-router-validation']):
            self.assertIn(key+': ${{ needs.'+dependency+'.result }}', required)
        success = {key: 'success' for key in keys}
        self.assertEqual(subprocess.run(['bash','-e','-c',script], env=dict(os.environ,**success),capture_output=True).returncode, 0)
        for key in keys:
            for result in ['failure','cancelled','skipped','']:
                self.assertNotEqual(subprocess.run(['bash','-e','-c',script],env=dict(os.environ,**(success | {key:result})),capture_output=True).returncode,0,(key,result))

    def test_admission_and_consumer_ownership(self):
        producer, consumer = job('pr-gate'), job('pr-gate-tests')
        self.assertIn('name: PR Gate / Build and tests', producer)
        self.assertIn('- name: Await exact-head review', producer)
        self.assertIn("github.run_attempt == 1", producer)
        self.assertIn('needs: pr-gate', consumer)
        # Docs-only still runs this job to success; only expensive steps skip.
        self.assertNotRegex(consumer.split('    steps:',1)[0],r'^    if:',)
        self.assertIn('phase: build',producer)
        self.assertIn('phase: test',consumer)
        self.assertIn('producer-attempt: ${{ needs.pr-gate.outputs.producer-attempt }}', consumer)
        self.assertIn('producer-attempt: ${{ steps.managed-producer.outputs.producer-attempt }}', producer)
        self.assertLess(consumer.index('Build missing managed test outputs'),consumer.index('phase: test'))
        self.assertIn('needs.pr-gate.outputs.skip !=',consumer)
        self.assertNotIn('continue-on-error: true',consumer.split('- name: Lean gate',1)[0])
        self.assertIn('Revalidate exact-head review before success', consumer)

    def test_review_admission_mutations_rejected(self):
        path = ROOT / 'scripts/ci/fixtures/validate-review-first-dispatch.py'
        spec = importlib.util.spec_from_file_location('review_contract', path)
        contract = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(contract)
        consumer = job('pr-gate-tests')
        mutations = [
            TEXT.replace('    needs: pr-gate\n', '    needs: format\n', 1),
            TEXT.replace('name: PR Gate / Build and tests', 'name: Renamed admission', 1),
        ]
        setup = consumer.split('- name: Setup .NET', 1)[1].split('- name:', 1)[0]
        gate = next(line for line in setup.splitlines() if line.strip().startswith('if:'))
        mutations.append(TEXT.replace(consumer, consumer.replace(gate, "        if: needs.pr-gate.outputs.skip != 'true'", 1)))
        mutations.append(TEXT.replace(consumer, consumer.replace('- name: Revalidate exact-head review before verification', '- name: Missing pre-execution review', 1)))
        with tempfile.TemporaryDirectory() as directory:
            candidate = Path(directory) / 'pr-gate.yml'
            contract.PR_GATE = candidate
            for source in mutations:
                candidate.write_text(source)
                with self.assertRaises((AssertionError, ValueError)):
                    contract.main()

    def test_same_canonical_selector_and_runtime_tests(self):
        producer = job('pr-gate')
        selector = job('affected-shards-select')
        self.assertIn('compute-affected-shards.py --cap "${CAP}" --annotate-resources',producer)
        self.assertIn('compute-affected-shards.py',selector)
        self.assertIn('AFFECTED_SHARDS_CAP',producer)
        self.assertIn('AFFECTED_SHARDS_CAP',selector)
        self.assertIn('--cap "${CAP}"', selector)
        self.assertIn('--annotate-resources', selector)
        consumer=job('pr-gate-tests')
        # These executable proofs must remain consumers of the producer build.
        for name in ['Prove remote source execution with real HTTP and PostGIS', 'PDAL']:
            self.assertIn(name,consumer)
        commands = re.findall(r'^          dotnet test (.*?)(?=\n\s*\n|\n\s*- name:|\Z)',consumer,re.S | re.M)
        self.assertEqual(len(commands), 8, 'All eight existing direct runtime proof invocations must remain')
        for command in commands:
            self.assertIn('--no-build',command)
            self.assertIn('--no-restore',command)
        shard=job('affected-shard')
        self.assertIn('needs: [affected-shards-select, pr-gate]',shard)
        self.assertIn('producer-attempt: ${{ needs.pr-gate.outputs.producer-attempt }}',shard)


if __name__ == '__main__':
    unittest.main()
