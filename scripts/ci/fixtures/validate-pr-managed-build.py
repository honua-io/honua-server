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
CI = (ROOT / '.github/workflows/ci.yml').read_text()


def job(name, source=TEXT):
    return re.search(r'^  '+re.escape(name)+r':\n(.*?)(?=^  [\w-]+:|\Z)', source, re.M | re.S).group(1)


class PrManagedBuildTests(unittest.TestCase):
    def test_required_results_fail_closed(self):
        required = job('required')
        self.assertIn('needs: [pr-gate, fast-tests, format, ci-router-validation, affected-shards]', required)
        self.assertIn('if: always()', required)
        script = re.search(r'        run: \|\n(.*?)(?=\n      #|\n      -|\Z)', required, re.S).group(1)
        script = textwrap.dedent(script)
        keys = ['BUILD_AND_TEST_RESULT', 'FAST_RESULT', 'FORMAT_RESULT', 'CI_ROUTER_RESULT',
                'AFFECTED_SHARDS_RESULT']
        dependencies = ['pr-gate', 'fast-tests', 'format', 'ci-router-validation', 'affected-shards']
        for key, dependency in zip(keys, dependencies):
            self.assertIn(key+': ${{ needs.'+dependency+'.result }}', required)
        success = {key: 'success' for key in keys}
        success['GITHUB_EVENT_NAME'] = 'pull_request'
        self.assertEqual(subprocess.run(['bash','-e','-c',script], env=dict(os.environ,**success),capture_output=True).returncode, 0)
        for key in keys:
            for result in ['failure','cancelled','skipped','']:
                self.assertNotEqual(subprocess.run(['bash','-e','-c',script],env=dict(os.environ,**(success | {key:result})),capture_output=True).returncode,0,(key,result))

    def test_fast_architecture_remains_independent_and_required(self):
        fast = job('fast-tests')
        self.assertNotIn('needs:', fast)
        self.assertIn('phase: all', fast)
        self.assertIn('build-scope: affected', fast)
        self.assertIn('Await exact-head review', fast)
        self.assertIn('fast-tests', job('required'))
        self.assertNotIn('Lean gate (fast unit and architecture verification)', job('runtime-proofs', CI))

    def test_full_ci_requires_runtime_proofs(self):
        import yaml
        full = yaml.safe_load(CI)
        required = full['jobs']['ci-gate']
        self.assertIn('runtime-proofs', required['needs'])
        step = required['steps'][0]
        self.assertEqual(step['env']['RUNTIME_PROOFS_RESULT'], '${{ needs.runtime-proofs.result }}')
        script = step['run'].replace("${{ contains(needs.*.result, 'failure') }}", 'false').replace("${{ contains(needs.*.result, 'cancelled') }}", 'false')
        for result in ('success', 'failure', 'cancelled', 'skipped', ''):
            env = dict(os.environ, REQUIRE_MANAGED_BUILD='true', BUILD_RESULT='success',
                       REQUIRE_FORMAT='true', FORMAT_RESULT='success', RUNTIME_PROOFS_RESULT=result)
            run = subprocess.run(['bash', '-e', '-c', script], env=env, capture_output=True)
            self.assertEqual(run.returncode == 0, result == 'success', result)
        self.assertIn("cron: '0 9 * * *'", CI)
        self.assertIn("cron: '0 21 * * *'", CI)
        self.assertNotIn('Build selected protocol profiles', job('pr-gate'))
        self.assertIn('Build selected protocol profiles', job('build', CI))

    def test_admission_and_consumer_ownership(self):
        producer, consumer = job('pr-gate'), job('runtime-proofs', CI)
        self.assertIn('name: PR Gate / Build and tests', producer)
        self.assertIn('- name: Await exact-head review', producer)
        self.assertIn("github.run_attempt == 1", producer)
        self.assertIn('needs: build', consumer)
        self.assertNotIn('  runtime-proofs:', TEXT)
        self.assertIn('phase: build',producer)
        self.assertIn('producer-attempt: ${{ needs.build.outputs.producer_attempt }}', consumer)
        self.assertIn('producer-attempt: ${{ steps.managed-producer.outputs.producer-attempt }}', producer)
        self.assertNotIn('needs.pr-gate', consumer)
        self.assertNotIn('continue-on-error: true',consumer.split('- name: Lean gate',1)[0])
        self.assertNotIn('REVIEW_FIRST_MODE', consumer)

    def test_review_admission_mutations_rejected(self):
        path = ROOT / 'scripts/ci/fixtures/validate-review-first-dispatch.py'
        spec = importlib.util.spec_from_file_location('review_contract', path)
        contract = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(contract)
        mutations = [TEXT.replace('name: PR Gate / Build and tests', 'name: Renamed admission', 1)]
        producer = job('pr-gate')
        for name in ('Setup .NET', 'Plan managed test producer', 'Lean gate (managed build producer)'):
            prefix, rest = producer.split('- name: ' + name, 1)
            step, suffix = rest.split('- name:', 1)
            gate = next(line for line in step.splitlines() if line.strip().startswith('if:'))
            altered_step = step.replace(gate, "        if: steps.docs-only.outputs.docs_only != 'true'", 1)
            altered = prefix + '- name: ' + name + altered_step + '- name:' + suffix
            mutations.append(TEXT.replace(producer, altered, 1))
        with tempfile.TemporaryDirectory() as directory:
            candidate = Path(directory) / 'pr-gate.yml'
            contract.PR_GATE = candidate
            for source in mutations:
                candidate.write_text(source)
                with self.assertRaises((AssertionError, ValueError)):
                    contract.main()

    def test_boot_proof_uses_producer_host_after_pristine_publication(self):
        producer, consumer = job('pr-gate'), job('runtime-proofs', CI)
        self.assertLess(producer.index('Pre-pull Testcontainers PostGIS'), producer.index('phase: build'))
        self.assertLess(producer.index('Publish pristine managed test outputs'), producer.index('Await Testcontainers PostGIS pre-pull'))
        self.assertLess(producer.index('Await Testcontainers PostGIS pre-pull'), producer.index('timeout 175s scripts/ci/server-boot-smoke.sh'))
        self.assertNotIn('server-boot-smoke.sh', consumer)
        proof = producer.split('- name: Honua.Server boot + migrations smoke', 1)[1].split('- name:', 1)[0]
        self.assertIn("steps.docs-only.outputs.docs_only != 'true'", proof)
        self.assertIn("steps.generated-only.outputs.generated_only != 'true'", proof)
        self.assertIn("env.REVIEW_FIRST_MODE != 'enforce'", proof)
        self.assertNotIn('continue-on-error', proof)

    def test_same_canonical_selector_and_runtime_tests(self):
        producer = job('pr-gate')
        selector = job('affected-shards-select')
        self.assertIn('compute-affected-shards.py --cap "${CAP}" --annotate-resources',producer)
        self.assertIn('compute-affected-shards.py',selector)
        self.assertIn('AFFECTED_SHARDS_CAP',producer)
        self.assertIn('AFFECTED_SHARDS_CAP',selector)
        self.assertIn('--cap "${CAP}"', selector)
        self.assertIn('--annotate-resources', selector)
        consumer=job('runtime-proofs', CI)
        # Server receipts stay in the managed consumer; native Worker coverage
        # uses the existing full-CI owner instead of compiling PDAL twice.
        categories = ['RemoteSourceExecutionProof', 'CopyFeaturesExecutionProof',
                      'LayerExecutionProof', 'ImageryClassifyExecutionProof',
                      'GeometryFormatExecutionProof', 'CalculateFieldExecutionProof']
        for category in categories:
            self.assertIn('Category=' + category, consumer)
        worker = job('worker-gdal-tests', CI)
        for marker in ['Category=RasterExecutionProof', 'Category=GDAL',
                       'Build production PDAL native tools for execution proofs',
                       'Prove the raster execution cases ran, not skipped']:
            self.assertIn(marker, worker)
        self.assertNotIn('Build production PDAL native tools', consumer)
        commands = re.findall(r'^          dotnet test (.*?)(?=\n\s*\n|\n\s*- name:|\Z)',consumer,re.S | re.M)
        self.assertEqual(len(commands), 5)
        for command in commands:
            self.assertIn('--no-build',command)
            self.assertIn('--no-restore',command)
        shard=job('affected-shard')
        self.assertIn('needs: [affected-shards-select, pr-gate]',shard)
        self.assertIn('producer-attempt: ${{ needs.pr-gate.outputs.producer-attempt }}',shard)


if __name__ == '__main__':
    unittest.main()
