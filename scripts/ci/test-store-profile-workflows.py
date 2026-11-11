import os
from pathlib import Path
import re
import subprocess
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[2]


def workflow(name):
    return yaml.load((ROOT / '.github/workflows' / name).read_text(), Loader=yaml.BaseLoader)


class StoreProfileWorkflowTests(unittest.TestCase):
    def test_serving_formats_smoke_disables_excluded_test_providers(self):
        script = (ROOT / 'scripts/ci/smoke-serving-formats.sh').read_text()
        override = script.split('cat > "$work/override.yml" <<EOF\n', 1)[1].split('\nEOF', 1)[0]
        environment = yaml.load(override, Loader=yaml.BaseLoader)['services']['honua-server']['environment']
        for provider in ('Redshift', 'Snowflake', 'Databricks'):
            with self.subTest(provider=provider):
                self.assertEqual(environment.get(f'Experimental__Features__{provider}Provider'), 'false')

    def test_release_bundle_promotes_verified_jit_postgis_digest(self):
        job = workflow('release-bundle.yml')['jobs']['rc-image']
        steps = job['steps']
        build = next(step for step in steps if step.get('id') == 'build')
        self.assertEqual(build['with']['file'], 'Dockerfile')
        self.assertIn('HONUA_STORE_PROFILE=postgis', build['with']['build-args'])
        verify = next(step for step in steps if step.get('name') == 'Verify RC JIT candidate before registry publication')
        self.assertIn('measure-serving-image.py "$CANDIDATE" --published --profile postgis --verify-jit-boundary', verify['run'])
        self.assertNotIn('--default-image', verify['run'], 'The AOT size target must not gate RC JIT promotion')
        self.assertIn('steps.build.outputs.digest', verify['env']['CANDIDATE'])
        text = (ROOT / '.github/workflows/release-bundle.yml').read_text()
        self.assertIn('${channel}-jit', text)
        self.assertIn(':latest-jit', text)
        self.assertNotIn('${channel}-aot', text)

    def test_shell_free_consumers_use_native_probe_and_external_http(self):
        jobs = workflow('security-nightly.yml')['jobs']
        steps = next(job['steps'] for job in jobs.values()
                     if any(step.get('id') == 'runtime-security' for step in job.get('steps', [])))
        security = next(step['run'] for step in steps if step.get('id') == 'runtime-security')
        self.assertIn('/app/honua-healthcheck --security', security)
        self.assertNotIn(' sh -c ', security)
        self.assertNotIn(' whoami', security)
        structure = next(step['run'] for step in steps if step.get('name') == 'Create structure test config')
        self.assertNotIn('commandTests:', structure)
        for name in ('wfs', 'wcs'):
            script = (ROOT / f'docker/cite/shared/tests/test-{name}-bootstrap.sh').read_text()
            self.assertNotIn('exec -T honua-server bash', script)

    def test_default_profile_verification_preserves_full_producer_and_runtime_proofs(self):
        jobs = workflow('pr-gate.yml')['jobs']
        producer_names = [step.get('name', '') for step in jobs['pr-gate']['steps']]
        verify_name = 'Verify default PostGIS store profile'
        self.assertNotIn(verify_name, producer_names)
        self.assertIn('Publish pristine managed test outputs', producer_names)
        self.assertIn('Honua.Server boot + migrations smoke', producer_names)
        consumer = workflow('ci.yml')['jobs']['runtime-proofs']
        self.assertEqual(consumer['needs'], 'build')
        steps = consumer['steps']
        names = [step.get('name', '') for step in steps]
        verify = steps[names.index(verify_name)]
        self.assertGreater(names.index(verify_name), names.index('Execute import and independent vector export fidelity proofs'))
        self.assertNotIn('Publish pristine managed test outputs', names)
        self.assertIn('-p:HonuaStoreProfile=postgis', verify['run'])
        self.assertIn('StoreProfileWritePolicyTests', verify['run'])
        self.assertIn('RunConfiguration.TreatNoTestsAsError=true', verify['run'])
        self.assertEqual(verify['env']['HONUA_EXPECTED_STORE_PROFILE'], 'postgis')
        self.assertNotIn('if', verify)

    def test_full_ci_can_measure_images_without_opening_a_pr(self):
        ci = workflow('ci.yml')
        self.assertEqual(ci['jobs'].get('store-image-proofs', {}).get('uses'),
                         './.github/workflows/store-image-size.yml')
        self.assertIn('workflow_call', workflow('store-image-size.yml')['on'])
        self.assertIn('store-image-proofs', ci['jobs']['ci-gate']['needs'])

    def test_selected_image_proofs_cannot_be_skipped_or_red(self):
        for result in ('failure', 'cancelled', 'skipped', ''):
            with self.subTest(result=result):
                process = self.run_gate('true', result)
                self.assertNotEqual(process.returncode, 0, process.stdout)

    def test_successful_or_unselected_image_proofs_leave_other_gates_intact(self):
        for required, result in (('true', 'success'), ('false', 'skipped')):
            with self.subTest(required=required, result=result):
                process = self.run_gate(required, result)
                self.assertEqual(process.returncode, 0, process.stderr)

    def test_publication_measures_every_variant_and_gates_default_before_promotion(self):
        job = workflow('deploy.yml')['jobs']['build-and-push']
        self.assertGreaterEqual(int(job['timeout-minutes']), 150,
                                'Native compilation must fit within the publication job budget')
        variants = {(row['image_key'], row['arch'], row['store_profile'])
                    for row in job['strategy']['matrix']['include']}
        self.assertEqual(variants, {(key, arch, profile)
                                   for key, profile in (('aot', 'postgis'), ('jit', 'postgis'), ('jit-full', 'full'))
                                   for arch in ('amd64', 'arm64')})
        steps = job['steps']
        names = [step.get('name', '') for step in steps]
        rows = job['strategy']['matrix']['include']
        for row in rows:
            self.assertEqual(row['tag_suffix'], {'jit': '', 'jit-full': '-full', 'aot': '-aot'}[row['image_key']])
        verify = steps[names.index('Verify serving candidate before registry publication')]
        self.assertIn('--published', verify['run'])
        self.assertNotIn('--default-image', verify['run'], 'The old AOT size target must not gate the JIT fallback')
        self.assertLess(names.index(verify['name']), names.index('Publish verified serving architecture tags'))
        self.assertNotIn('if', verify)
        self.assertEqual(verify['env']['IMAGE_TAGS'], '${{ steps.arch_tags.outputs.tags }}')
        manifests = workflow('deploy.yml')['jobs']['publish-manifests']['strategy']['matrix']['include']
        self.assertEqual([(row['tag_suffix'], row['compatibility_alias']) for row in manifests],
                         [('', '-jit'), ('-full', ''), ('-aot', '')])
        upload = steps[names.index('Upload exact published image sizes')]
        self.assertEqual(upload['if'], 'always()')
        self.assertEqual(upload['with']['if-no-files-found'], 'error')

    def test_optional_aot_cannot_block_default_publication(self):
        jobs = workflow('deploy.yml')['jobs']
        self.assertEqual(jobs['build-and-push'].get('continue-on-error'), "${{ matrix.image_key == 'aot' }}")
        self.assertEqual(jobs['publish-manifests'].get('continue-on-error'), "${{ matrix.tag_suffix == '-aot' }}")
        nightly = workflow('nightly-container-build.yml')['jobs']
        self.assertIn('manifest-jit', nightly['compose-executor']['needs'])
        self.assertNotIn('manifest-aot', nightly['compose-executor']['needs'])
        aot_meta = next(step for step in nightly['manifest-aot']['steps'] if step.get('id') == 'meta')
        self.assertIn('value=nightly-aot', aot_meta['with']['tags'])
        self.assertNotIn('value=trunk\n', aot_meta['with']['tags'])
        jit_meta = next(step for step in nightly['manifest-jit']['steps'] if step.get('id') == 'meta')
        self.assertIn('value=nightly${{ matrix.store_suffix }}', jit_meta['with']['tags'])
        self.assertIn('value=trunk${{ matrix.store_suffix }}', jit_meta['with']['tags'])

    def test_lifecycle_proves_manual_legacy_and_automated_compatible_rollback(self):
        steps = workflow('compose-release-lifecycle.yml')['jobs']['compose-release-lifecycle']['steps']
        catalog = next(step for step in steps if step.get('with', {}).get('repository') == 'honua-io/honua-release')
        self.assertRegex(catalog['with']['ref'], r'^[a-f0-9]{40}$')
        self.assertEqual(catalog['with']['persist-credentials'], 'false')
        journey = next(step for step in steps if 'tests/integration/test_compose_release_lifecycle.py' in step.get('run', ''))
        self.assertIn('install pytest jsonschema', journey['run'])
        self.assertIn('HONUA_DEPLOYMENT_CONFIGURATION_ROOT', journey['env'])
        self.assertLess(journey['run'].index('tests/integration/test_compose_manual_rc1_rollback.py'),
                        journey['run'].index('tests/integration/test_compose_release_lifecycle.py'))

    def test_nightly_jit_tag_mapping_uses_existing_architecture_tags(self):
        job = workflow('nightly-container-build.yml')['jobs']['manifest-jit']
        script = next(step['run'] for step in job['steps'] if step.get('id') == 'manifest')
        mapping = script[script.index('repository='):script.index('docker buildx imagetools create')]
        for tag, source in [('nightly', 'nightly-jit'), ('trunk', 'trunk-jit'),
                            ('nightly-full', 'nightly-jit-full'), ('trunk-full', 'trunk-jit-full'),
                            ('nightly-abc1234-full', 'nightly-jit-abc1234-full'),
                            ('nightly-jit', 'nightly-jit'), ('trunk-jit-full', 'trunk-jit-full')]:
            with self.subTest(tag=tag):
                result = subprocess.run(['bash'], input=mapping + '\nprintf "%s" "$source_tag"',
                                        env={**os.environ, 'tag': 'ghcr.io/honua/server:' + tag},
                                        text=True, capture_output=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout, 'ghcr.io/honua/server:' + source)

    def run_gate(self, required, result):
        script = workflow('ci.yml')['jobs']['ci-gate']['steps'][0]['run']
        script = re.sub(r'\$\{\{.*?\}\}', 'false', script)
        environment = {**os.environ, 'REQUIRE_MANAGED_BUILD': 'false', 'REQUIRE_FORMAT': 'false',
                       'REQUIRE_STORE_IMAGES': required, 'STORE_IMAGE_RESULT': result}
        return subprocess.run(['bash'], input=script, env=environment, text=True, capture_output=True)


if __name__ == '__main__':
    unittest.main()
