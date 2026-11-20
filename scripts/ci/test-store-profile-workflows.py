import os
import json
from pathlib import Path
import re
import subprocess
import tempfile
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[2]
_RELEASE_REPOSITORY = 'ghcr.io/honua-io/honua-server@'
_RELEASE_PLATFORM_DIGEST = 'sha256:' + 'a' * 64
_RELEASE_REQUESTED_DIGEST = 'sha256:' + 'b' * 64
_RELEASE_PULL_IDS = (_RELEASE_REPOSITORY, _RELEASE_PLATFORM_DIGEST, _RELEASE_REQUESTED_DIGEST)
_RELEASE_DOCKER = '''docker() {
  if [[ "${1-}" == login ]]; then return 0; fi
  if [[ "${1-} ${2-} ${3-} ${4-}" == "buildx imagetools inspect --raw" ]]; then printf '%s' "$SOURCE_MANIFEST"; return 0; fi
  if [[ "${1-}" == pull ]]; then return 0; fi
  if [[ "${1-} ${2-}" == "image inspect" ]]; then printf '%s' "$INSPECT_JSON"; return 0; fi
  echo "unexpected docker $*" >&2
  return 1
}
'''


def _run_release_pull():
    revision = 'e' * 40
    index = {'mediaType': 'application/vnd.oci.image.index.v1+json', 'manifests': [
        {'mediaType': 'application/vnd.oci.image.manifest.v1+json', 'digest': _RELEASE_PLATFORM_DIGEST,
         'platform': {'os': 'linux', 'architecture': 'amd64'}}]}
    inspected = [{'RepoDigests': [_RELEASE_REPOSITORY + _RELEASE_PLATFORM_DIGEST],
                  'Os': 'linux', 'Architecture': 'amd64', 'Id': 'sha256:' + 'c' * 64,
                  'Config': {'Labels': {'org.opencontainers.image.revision': revision}}}]
    with tempfile.TemporaryDirectory() as directory:
        work = Path(directory)
        (work / 'scripts').symlink_to(ROOT / 'scripts', target_is_directory=True)
        environment = dict(os.environ, SCAN_IMAGE=_RELEASE_REPOSITORY + _RELEASE_REQUESTED_DIGEST,
                           SCAN_PLATFORM='linux/amd64', SCAN_REVISION=revision,
                           SOURCE_MANIFEST=json.dumps(index), INSPECT_JSON=json.dumps(inspected),
                           REGISTRY_TOKEN='unused', GITHUB_ACTOR='actor', RUNNER_TEMP=str(work),
                           GITHUB_ENV=str(work / 'env'), GITHUB_SERVER_URL='https://github.com',
                           GITHUB_REPOSITORY='honua-io/honua-server', GITHUB_RUN_ID='1')
        subprocess.run(['bash', '-c', _RELEASE_DOCKER + 'export -f docker\nbash scripts/security/bind-platform-image.sh'],
                       cwd=work, env=environment, check=True)
        identity = json.loads((work / 'existing-image-identity.json').read_text())
        return (work / 'env').read_text(), identity, (work / 'requested-manifest.json').is_file()


def workflow(name):
    return yaml.load((ROOT / '.github/workflows' / name).read_text(), Loader=yaml.BaseLoader)


class StoreProfileWorkflowTests(unittest.TestCase):
    def test_derived_zarr_worker_uses_serving_host_job_database(self):
        base = yaml.safe_load((ROOT / 'docker/cng/compose.yml').read_text())
        overlay = yaml.safe_load((ROOT / 'docker/cng/derived-zarr.yml').read_text())
        server_environment = dict(entry.split('=', 1) for entry in
                                  base['services']['honua-server']['environment'])
        worker = overlay['services']['zarr-worker']
        self.assertEqual(worker['environment']['ConnectionStrings__DefaultConnection'],
                         server_environment['ConnectionStrings__DefaultConnection'])
        self.assertEqual(worker['depends_on']['postgres']['condition'], 'service_healthy')
        self.assertIn('healthcheck', base['services']['postgres'])

    def test_serving_formats_smoke_disables_excluded_test_providers(self):
        script = (ROOT / 'scripts/ci/smoke-serving-formats.sh').read_text()
        override = script.split('cat > "$work/override.yml" <<EOF\n', 1)[1].split('\nEOF', 1)[0]
        environment = yaml.load(override, Loader=yaml.BaseLoader)['services']['honua-server']['environment']
        for provider in ('Redshift', 'Snowflake', 'Databricks'):
            with self.subTest(provider=provider):
                self.assertEqual(environment.get(f'Experimental__Features__{provider}Provider'), 'false')

    def test_conformance_stacks_disable_excluded_test_providers(self):
        stacks = sorted((ROOT / 'docker/cite').glob('*/compose.yml'))
        stacks.append(ROOT / 'docker/cng/compose.yml')
        self.assertGreater(len(stacks), 1)
        for path in stacks:
            compose = yaml.load(path.read_text(), Loader=yaml.BaseLoader)
            for name, service in compose['services'].items():
                environment = service.get('environment', {})
                if isinstance(environment, list):
                    environment = dict(entry.split('=', 1) for entry in environment if '=' in entry)
                if environment.get('ASPNETCORE_ENVIRONMENT') != 'Test':
                    continue
                for provider in ('Redshift', 'Snowflake', 'Databricks'):
                    with self.subTest(stack=str(path.relative_to(ROOT)), service=name, provider=provider):
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

    def test_release_bundle_reports_rc_budget(self):
        steps = workflow('release-bundle.yml')['jobs']['rc-image']['steps']
        verify = next(step for step in steps if step.get('name') == 'Verify RC JIT candidate before registry publication')
        self.assertIn('--release-stage rc', verify['run'])

    def test_release_inventory_registers_resolved_platform_manifest(self):
        jobs = workflow('release-security-assets.yml')['jobs']
        assets = jobs['assets']
        manifest = next(step for step in assets['steps']
                        if step.get('name') == 'Resolve the attestation-free platform manifest')
        self.assertIn('platform_ref=$platform_ref', manifest['run'])
        self.assertIn('>> "$GITHUB_OUTPUT"', manifest['run'])
        self.assertEqual(assets['outputs']['platform_ref'],
                         '${{ steps.' + manifest['id'] + '.outputs.platform_ref }}')
        register = jobs['register']
        self.assertEqual(register['env']['IMAGE'], '${{ needs.assets.outputs.platform_ref }}')
        command = next(step['run'] for step in register['steps']
                       if step.get('name') == 'Add the digest to the supported inventory')
        self.assertIn('--image "$IMAGE"', command)
        self.assertIn("--image '$IMAGE'", command)

    def test_release_manifest_step_exports_platform_digest_for_index_and_direct_inputs(self):
        steps = workflow('release-security-assets.yml')['jobs']['assets']['steps']
        script = next(step['run'] for step in steps
                      if step.get('name') == 'Resolve the attestation-free platform manifest')
        script = script.replace('${{ steps.names.outputs.asset_name }}', 'release-test')
        manifest_type = 'application/vnd.oci.image.manifest.v1+json'
        manifest = {'schemaVersion': 2, 'mediaType': manifest_type,
                    'config': {'mediaType': 'application/vnd.oci.image.config.v1+json',
                               'digest': 'sha256:' + 'c' * 64, 'size': 2}, 'layers': []}
        repository = 'ghcr.io/honua-io/honua-server@'
        platform_digest = 'sha256:' + 'a' * 64
        index = {'mediaType': 'application/vnd.oci.image.index.v1+json', 'manifests': [
            {'mediaType': manifest_type, 'digest': platform_digest,
             'platform': {'os': 'linux', 'architecture': 'amd64'}}]}
        docker = '''docker() {
  if [[ "$5" == "$SCAN_IMAGE" ]]; then
    printf '%s' "$SOURCE_MANIFEST"
  else
    printf '%s' "$PLATFORM_MANIFEST"
  fi
}
'''
        for source, digest in ((index, 'sha256:' + 'b' * 64), (manifest, platform_digest)):
            with self.subTest(index=source is index), tempfile.TemporaryDirectory() as directory:
                work = Path(directory)
                (work / 'scripts').symlink_to(ROOT / 'scripts', target_is_directory=True)
                output = work / 'output'
                environment = dict(os.environ, SCAN_IMAGE=repository + digest, SCAN_PLATFORM='linux/amd64',
                                   SOURCE_MANIFEST=json.dumps(source), PLATFORM_MANIFEST=json.dumps(manifest),
                                   GITHUB_ENV=str(work / 'env'), GITHUB_OUTPUT=str(output))
                subprocess.run(['bash', '-c', docker + script], cwd=work, env=environment, check=True)
                self.assertEqual(output.read_text().strip(), 'platform_ref=' + repository + platform_digest)
                self.assertEqual((work / 'release-test.attestation-free-ref.txt').read_text().strip(),
                                 repository + platform_digest)

    def test_release_scan_keeps_the_published_digest_in_the_vex_document(self):
        steps = workflow('release-security-assets.yml')['jobs']['assets']['steps']
        pull = next(step['run'] for step in steps
                    if step.get('name') == 'Pull the release image and bind its identity')
        vex = next(step['run'] for step in steps
                   if step.get('name') == 'Generate the release OpenVEX document')
        trivy = next(step for step in steps if step.get('name') == 'Scan with the release VEX applied')
        self.assertIn('bash scripts/security/bind-platform-image.sh', pull)
        self.assertIn('--image "$SCAN_IMAGE"', vex)
        self.assertIn('"$PLATFORM_REF" != "$SCAN_IMAGE"', vex)
        script = (ROOT / 'scripts/security/bind-platform-image.sh').read_text()
        persisted = script.split('>> "$GITHUB_ENV"', 1)[0].rsplit('{', 1)[1]
        self.assertEqual(['echo "REQUESTED_IMAGE=$requested"', 'echo "RESOLVED_IMAGE=$resolved"'],
                         [line.strip() for line in persisted.splitlines() if line.strip() and line.strip() != '}'])
        self.assertEqual(trivy['with']['image-ref'], '${{ env.RESOLVED_IMAGE }}')
        saved, identity, manifest = _run_release_pull()
        repository, platform_digest, requested_digest = _RELEASE_PULL_IDS
        self.assertIn('RESOLVED_IMAGE=' + repository + platform_digest, saved)
        self.assertIn('REQUESTED_IMAGE=' + repository + requested_digest, saved)
        self.assertNotIn('SCAN_IMAGE=', saved)
        self.assertEqual(repository + platform_digest, identity['image'])
        self.assertEqual(repository + requested_digest, identity['requestedImage'])
        self.assertTrue(manifest)

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
        self.assertEqual(consumer['needs'], ['build', 'changes'])
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

    def test_store_measurement_uses_heavy_runner_and_separate_profile_caches(self):
        jobs = workflow('store-image-size.yml')['jobs']
        job = jobs['measure']
        selector = jobs['runner-select']
        self.assertEqual(selector['uses'], './.github/workflows/advisory-runner-select.yml')
        self.assertEqual(selector['with']['hosted-runner'], 'ubuntu-24.04')
        self.assertEqual(job['needs'], 'runner-select')
        self.assertEqual(job['runs-on'], "${{ (!github.event.pull_request || github.event.pull_request.head.repo.full_name == github.repository) && fromJSON(needs.runner-select.outputs.runner || '\"ubuntu-24.04\"') || 'ubuntu-latest' }}")
        self.assertIn('always() && !cancelled()', job['if'])
        admission = workflow('advisory-runner-select.yml')['jobs']['select']
        # honua-light hosts only offer the snap AWS CLI, which cannot start in the runner sandbox.
        self.assertEqual(admission['runs-on'], 'ubuntu-latest')
        budgets = re.fullmatch(
            r"\$\{\{ matrix.compilation == 'aot' && (\d+) \|\| (\d+) \}\}",
            job['timeout-minutes'])
        self.assertIsNotNone(budgets, 'Store builds must use separate AOT and JIT budgets')
        aot_minutes, jit_minutes = map(int, budgets.groups())
        nightly_aot = workflow('nightly-container-build.yml')['jobs']['build-aot']
        self.assertEqual(aot_minutes, int(nightly_aot['timeout-minutes']))
        self.assertGreater(aot_minutes, jit_minutes)
        self.assertGreater(jit_minutes, 0)
        self.assertLessEqual(jit_minutes, 30)
        build = next(step for step in job['steps'] if step.get('name') == 'Build serving image')
        self.assertIn('matrix.profile', build['with']['cache-to'])
        self.assertIn('matrix.compilation', build['with']['cache-to'])
        self.assertIn('mode=max', build['with']['cache-to'])

    def test_store_matrix_jobs_use_isolated_local_images(self):
        job = workflow('store-image-size.yml')['jobs']['measure']
        reference = job.get('env', {}).get('IMAGE', '')
        for dimension in ('github.run_id', 'github.run_attempt', 'matrix.profile', 'matrix.compilation'):
            self.assertIn(dimension, reference)
        build = next(step for step in job['steps'] if step.get('name') == 'Build serving image')
        self.assertEqual(build['with']['tags'], '${{ env.IMAGE }}')
        text = (ROOT / '.github/workflows/store-image-size.yml').read_text()
        self.assertNotIn('honua-store-measure:current', text)
        for step in job['steps']:
            script = step.get('run', '')
            if any(command in script for command in ('docker image inspect', 'docker create',
                                                     'docker run --rm --env', 'smoke-serving-formats.sh',
                                                     'scripts/ci/measure-serving-image.py ', 'for version in before after')):
                self.assertIn('"$IMAGE"', script)

    def test_store_build_context_does_not_change_when_evidence_is_exported(self):
        job = workflow('store-image-size.yml')['jobs']['measure']
        prepare = next(step for step in job['steps'] if step.get('name') == 'Freeze serving build context')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / 'source'
            source.mkdir()
            subprocess.run(['git', 'init', '-q', str(source)], check=True)
            (source / 'Dockerfile').write_text('FROM scratch\n')
            subprocess.run(['git', 'add', 'Dockerfile'], cwd=source, check=True)
            subprocess.run(['git', '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid',
                            'commit', '-qm', 'fixture'], cwd=source, check=True)
            temporary = root / 'runner'
            temporary.mkdir()
            environment = {**os.environ, 'RUNNER_TEMP': str(temporary)}
            subprocess.run(['bash', '-euo', 'pipefail', '-c', prepare['run']],
                           cwd=source, env=environment, check=True)
            context = temporary / 'store-image-context'
            self.assertEqual((context / 'Dockerfile').read_text(), 'FROM scratch\n')
            (source / 'artifacts').mkdir()
            (source / 'artifacts/image.oci.tar').write_bytes(b'exported image')
            self.assertFalse((context / 'artifacts').exists())
        builds = [step for step in job['steps'] if step.get('uses', '').startswith('docker/build-push-action@')]
        self.assertEqual(len(builds), 3)
        for build in builds:
            self.assertEqual(build['with']['context'], '${{ runner.temp }}/store-image-context')
            self.assertIn('runner.temp', build['with']['file'])
        concurrency = workflow('store-image-size.yml')['concurrency']
        self.assertIn('github.event.pull_request.number', concurrency['group'])
        self.assertEqual(concurrency['cancel-in-progress'], 'true')

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

    def test_selected_full_runs_require_both_interop_proofs(self):
        for name in ('GEOPARQUET_RESULT', 'GEOARROW_RESULT'):
            for result in ('failure', 'cancelled', 'skipped', ''):
                with self.subTest(name=name, result=result):
                    process = self.run_gate('true', 'success', **{name: result})
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

    def test_tag_release_aot_stays_optional(self):
        jobs = workflow('deploy.yml')['jobs']
        self.assertEqual(jobs['build-and-push'].get('continue-on-error'), "${{ matrix.image_key == 'aot' }}")
        self.assertEqual(jobs['publish-manifests'].get('continue-on-error'), "${{ matrix.tag_suffix == '-aot' }}")

    def test_nightly_default_tags_and_customer_package_use_aot(self):
        nightly = workflow('nightly-container-build.yml')['jobs']
        executor = nightly['compose-executor']
        self.assertIn('manifest-aot', executor['needs'])
        self.assertNotIn('manifest-jit', executor['needs'])
        executor_text = json.dumps(executor)
        self.assertIn('needs.manifest-aot.outputs.image', executor_text)
        self.assertNotIn('needs.manifest-jit.outputs.image', executor_text)
        jit_meta = next(step for step in nightly['manifest-jit']['steps'] if step.get('id') == 'meta')
        bare = [line for line in jit_meta['with']['tags'].splitlines()
                if re.fullmatch(r'type=raw,value=(nightly|trunk)(-\$\{\{ needs\.[^}]+\}\})?\$\{\{ matrix\.store_suffix \}\}.*', line)]
        self.assertEqual(len(bare), 4)
        for line in bare:
            self.assertTrue(line.endswith(",enable=${{ matrix.store_suffix != '' }}"), line)
        jit_script = next(step['run'] for step in nightly['manifest-jit']['steps'] if step.get('id') == 'manifest')
        self.assertIn('subject_ref="${subject_name}:nightly-jit-${{ needs.select-candidate.outputs.sha7 }}"', jit_script)

    def test_nightly_aot_tags_publish_their_default_alias_in_one_index(self):
        job = workflow('nightly-container-build.yml')['jobs']['manifest-aot']
        script = next(step['run'] for step in job['steps'] if step.get('id') == 'manifest')
        mapping = script[script.index('repository='):script.index('destinations=')]
        create = script[script.index('destinations='):script.index('docker buildx imagetools create')]
        self.assertIn('"$tag" "$default_tag"', create)
        for tag, alias in [('nightly-aot', 'nightly'), ('trunk-aot', 'trunk'),
                           ('nightly-aot-20261117', 'nightly-20261117'), ('nightly-aot-abc1234', 'nightly-abc1234')]:
            with self.subTest(tag=tag):
                result = subprocess.run(['bash'], input=mapping + '\nprintf "%s" "$default_tag"',
                                        env={**os.environ, 'tag': 'ghcr.io/honua/server:' + tag},
                                        text=True, capture_output=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout, 'ghcr.io/honua/server:' + alias)
        result = subprocess.run(['bash'], input=mapping, env={**os.environ, 'tag': 'ghcr.io/honua/server:nightly-jit'},
                                text=True, capture_output=True)
        self.assertNotEqual(result.returncode, 0)

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
        candidate = next(step for step in steps if step.get('name') == 'Build production candidate image')
        self.assertEqual(candidate['with']['file'], 'docker/Dockerfile.aot')
        self.assertIn('buildcache:honua-aot-amd64', candidate['with']['cache-from'])

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

    def run_gate(self, required, result, **overrides):
        script = workflow('ci.yml')['jobs']['ci-gate']['steps'][0]['run']
        script = re.sub(r'\$\{\{.*?\}\}', 'false', script)
        # The full-run interop proofs share the store images' selection.
        environment = {**os.environ, 'REQUIRE_MANAGED_BUILD': 'false', 'REQUIRE_FORMAT': 'false',
                       'REQUIRE_STORE_IMAGES': required, 'STORE_IMAGE_RESULT': result,
                       'GEOPARQUET_RESULT': result, 'GEOARROW_RESULT': result, **overrides}
        return subprocess.run(['bash'], input=script, env=environment, text=True, capture_output=True)


if __name__ == '__main__':
    unittest.main()
