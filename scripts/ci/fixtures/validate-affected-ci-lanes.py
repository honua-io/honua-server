#!/usr/bin/env python3
"""Guard expensive trailing lanes without changing selection or PR/full coverage."""
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location('affected_ci_lanes', ROOT / 'scripts/ci/affected-ci-lanes.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
TRAILING_SPEC = importlib.util.spec_from_file_location(
    'trailing_selection', ROOT / 'scripts/ci/trailing_selection.py')
TRAILING = importlib.util.module_from_spec(TRAILING_SPEC)
TRAILING_SPEC.loader.exec_module(TRAILING)


class AffectedLaneTests(unittest.TestCase):
    def test_unknown_shared_and_empty_changes_fail_closed(self):
        for paths in [[], ['unknown-input'], ['Directory.Build.props'],
                      ['src/Honua.Core/Core/Types.cs'], ['src/Honua.Hosting/Startup.cs'],
                      ['src/Honua.Server/Startup/JsonContextRegistration.cs'],
                      ['src/Honua.Server/EndpointRegistry.cs'],
                      ['tests/seed/base-schema.sql'], ['tests/seed/client-compat-v1.sql']]:
            with self.subTest(paths=paths):
                self.assertTrue(all(MODULE.select(paths).values()))

    def test_shared_setup_actions_keep_their_consumers(self):
        server = MODULE.select(['.github/actions/setup-honua-server/action.yml'])
        self.assertEqual(
            {lane for lane, enabled in server.items() if enabled},
            {'browser', 'mcp', 'postgres'},
        )
        mcp = MODULE.select(['.github/actions/setup-honua-mcp/action.yml'])
        self.assertEqual(
            {lane for lane, enabled in mcp.items() if enabled},
            {'mcp', 'postgres'},
        )

    def test_composite_action_selection_handles_unused_nested_and_fail_closed(self):
        self.assertFalse(any(MODULE.select(['.github/actions/format-check/action.yml']).values()))
        self.assertEqual(
            {lane for lane, enabled in MODULE.select(['.github/actions/managed-test-artifacts/action.yml']).items() if enabled},
            {'runtime'},
        )
        dotnet = MODULE.select(['.github/actions/setup-dotnet-ci/action.yml'])
        self.assertEqual(
            {lane for lane, enabled in dotnet.items() if enabled},
            {'aot', 'browser', 'mcp', 'postgres', 'python', 'runtime'},
        )
        for fail_closed_path in [
            '.github/actions/does-not-exist/action.yml',
            '.github/actions/README.md',
        ]:
            with self.subTest(path=fail_closed_path):
                self.assertTrue(all(MODULE.select([fail_closed_path]).values()))
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / '.github/workflows').mkdir(parents=True)
            (root / '.github/workflows/ci.yml').write_text((ROOT / '.github/workflows/ci.yml').read_text())
            broken = root / '.github/actions/broken'
            broken.mkdir(parents=True)
            (broken / 'action.yml').write_text('runs: [unterminated\n')
            self.assertTrue(all(MODULE.select(['.github/actions/broken/action.yml'], repo_root=root).values()))

    def test_cyclic_composite_actions_terminate_and_select_consuming_lane(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / '.github/workflows').mkdir(parents=True)
            (root / '.github/workflows/ci.yml').write_text(
                'jobs:\n'
                + ''.join(
                    f'  {job_id}:\n    steps:\n      - uses: ./.github/actions/cycle-a\n'
                    if lane == 'runtime'
                    else f'  {job_id}:\n    steps: []\n'
                    for lane, job_ids in MODULE.LANE_JOBS.items()
                    for job_id in job_ids
                )
            )
            for name, target in [('cycle-a', 'cycle-b'), ('cycle-b', 'cycle-a')]:
                action_dir = root / '.github/actions' / name
                action_dir.mkdir(parents=True)
                (action_dir / 'action.yml').write_text(
                    f'runs:\n  using: composite\n  steps:\n    - uses: ./.github/actions/{target}\n'
                )
            cyclic = MODULE.select(['.github/actions/cycle-b/action.yml'], repo_root=root)
            self.assertEqual({lane for lane, enabled in cyclic.items() if enabled}, {'runtime'})

    def test_lane_jobs_exist_in_ci_yml_and_gate_on_their_lane(self):
        jobs = yaml.load((ROOT / '.github/workflows/ci.yml').read_text(), Loader=yaml.BaseLoader)['jobs']
        self.assertEqual(set(MODULE.LANE_JOBS), set(MODULE.LANES))
        mapped_jobs = {}
        for lane, job_ids in MODULE.LANE_JOBS.items():
            self.assertTrue(job_ids, f'Lane {lane} must map to at least one ci.yml job')
            gate_token = f'fromJson(needs.changes.outputs.specialized_lanes).{lane}'
            for job_id in job_ids:
                self.assertIn(job_id, jobs)
                self.assertIn(gate_token, jobs[job_id].get('if', ''))
                mapped_jobs[job_id] = lane
        for job_id, job in jobs.items():
            condition = job.get('if', '')
            if 'specialized_lanes).' in condition:
                self.assertIn(job_id, mapped_jobs, f'ci.yml job {job_id} gates on specialized_lanes but is missing from LANE_JOBS')

    def test_styling_and_mcp_seed_keep_direct_consumers(self):
        self.assertTrue(MODULE.select(['src/Honua.Server/Features/Styling/StyleEndpoints.cs'])['browser'])
        self.assertTrue(MODULE.select(['tests/seed/mcp.yaml'])['mcp'])

    def test_shared_seeds_keep_all_compatibility_consumers(self):
        for path in ['tests/seed/client-compat-v1.sql', 'tests/seed/base-schema.sql']:
            with self.subTest(path=path):
                self.assertTrue(all(MODULE.select([path]).values()))

    def test_scoped_unit_change_does_not_start_compatibility_fanout(self):
        self.assertFalse(any(MODULE.select(['tests/dotnet/Honua.Server.Tests/Utilities/ParserTests.cs']).values()))

    def test_each_lane_selects_its_own_inputs(self):
        inputs = {
            'aot': 'tests/native/AotSmoke.cs', 'docker': 'docker/Dockerfile',
            'postgres': 'src/Honua.Db/Postgres/Query.cs',
            'browser': 'tests/js-browser/maplibre.spec.ts',
            'python': 'tests/python/test_query.py', 'mcp': 'src/Honua.Ai/Features/Grounding/Tool.cs',
            'quickstart': 'scripts/docker/quickstart.sh',
            'runtime': 'scripts/ci/run-import-export-fidelity-proof.py',
        }
        for lane, path in inputs.items():
            with self.subTest(lane=lane):
                self.assertTrue(MODULE.select([path])[lane])

    def test_runtime_and_publish_inputs_keep_native_and_container_verification(self):
        for path in ['src/Honua.Protocols/Stac/SearchEndpoints.cs',
                     'src/Honua.Protocols/Stac/Honua.Protocols.Stac.csproj',
                     'src/Honua.Scene/SceneUrls.cs']:
            with self.subTest(path=path):
                selected = MODULE.select([path])
                self.assertTrue(selected['aot'])
                self.assertTrue(selected['docker'])

    def test_aot_lane_ignores_non_src_projects_and_keeps_scene_fixture_check(self):
        for path in ['docker/worker-customcode-dotnet/harness/warm/Harness.csproj',
                     'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj']:
            with self.subTest(non_src_project=path):
                selected = MODULE.select([path])
                self.assertFalse(selected['aot'])
                self.assertFalse(selected['postgres'])
        for path in ['src/Honua.Server/Honua.Server.csproj',
                     'tests/native/AotSmoke.cs',
                     'src/Honua.Server/rd.xml',
                     'scripts/ci/verify-scene-fixture-publication.py',
                     'tests/python/test_scene_fixture_publication.py']:
            with self.subTest(aot_input=path):
                self.assertTrue(MODULE.select([path])['aot'])

    def test_runtime_lane_selects_proof_inputs_and_skips_unrelated_scripts(self):
        for path in [
            'src/Honua.Server/Program.cs',
            'docker/worker-gdal/Dockerfile',
            'docker/runtime/entrypoint.sh',
            'tests/fixtures/sample.gpkg',
            'tests/dotnet/Honua.Server.Tests/Features/Geoprocessing/Execution/LayerSourceExecutionProofTests.cs',
            'tests/dotnet/Honua.Server.Tests/Features/Eval/McpQueryWildcardPostgresIntegrationTests.cs',
            'tests/dotnet/Honua.Server.Tests/Infrastructure/TestServerFixture.cs',
            'tests/dotnet/Honua.Server.Tests/Features/Export/Writers/IndependentVectorExportFixtureTests.cs',
            'tests/dotnet/Honua.Db.Postgres.Tests/Features/Import/StreamingFileImportStagingTableTests.cs',
            'tests/dotnet/Honua.Db.Postgres.Tests/Features/Security/StoreProfileWritePolicyTests.cs',
            'tests/dotnet/Honua.Core.Tests/Features/Security/StoreProfileWritePolicyTests.cs',
            'tests/python/unit/test_import_export_fidelity_proof.py',
            'tests/python/unit/test_diff_baselines.py',
            'tests/baselines/client-compat/qgis/sample.cert.json',
            'docs/gis/data/client-certification-matrix.v1.json',
            'docs/gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md',
            'scripts/client-compat/diff-baselines.py',
            'scripts/ci/fixtures/import-export-fidelity-baseline.json',
            'scripts/ci/run-import-export-fidelity-proof.py',
            'scripts/ci/decode-vector-export-proof.py',
            'scripts/ci/prepull-pr-gate-proof-images.sh',
        ]:
            with self.subTest(runtime_input=path):
                self.assertTrue(MODULE.select([path])['runtime'])
        self.assertFalse(MODULE.select([
            'scripts/ci/affected-ci-lanes.py',
            'tests/python/unit/test_other.py',
        ])['runtime'])
        jobs = yaml.load((ROOT / '.github/workflows/ci.yml').read_text(), Loader=yaml.BaseLoader)['jobs']
        runtime_if = jobs['runtime-proofs']['if']
        self.assertIn("needs.changes.outputs.full_ci == 'true'", runtime_if)
        self.assertIn('fromJson(needs.changes.outputs.specialized_lanes).runtime', runtime_if)
        self.assertIn('changes', jobs['runtime-proofs']['needs'])
        gate_env = jobs['ci-gate']['steps'][0]['env']
        self.assertIn('fromJson(needs.changes.outputs.specialized_lanes).runtime', gate_env['REQUIRE_RUNTIME_PROOFS'])
        self.assertIn("needs.changes.outputs.full_ci == 'true'", gate_env['REQUIRE_RUNTIME_PROOFS'])

    def test_mcp_backend_changes_keep_certification(self):
        for path in ['src/Honua.Server/Features/Protocols/Grpc/HonuaFeatureService.cs',
                     'src/Honua.Server/Features/FeatureServer/Query.cs',
                     'src/Honua.Protocols/Stac/SearchEndpoints.cs']:
            with self.subTest(path=path):
                self.assertTrue(MODULE.select([path])['mcp'])

    def test_protocol_adapters_select_wire_compatibility(self):
        selected = MODULE.select(['src/Honua.Server/Features/Protocols/OgcFeatures/Query.cs'])
        self.assertTrue(selected['browser'])
        self.assertTrue(selected['python'])

    def test_shared_browser_harness_and_seed_changes_run_browser_suites(self):
        for path in ['tests/shared/js-bootstrap.ts', 'tests/seed/browser-compat.yaml']:
            with self.subTest(path=path):
                self.assertTrue(MODULE.select([path])['browser'])

    def test_compatibility_prerequisites_still_run(self):
        for path in ['src/Honua.Server/Program.cs', 'src/Honua.Ai/Features/Grounding/Tool.cs']:
            self.assertTrue(MODULE.select([path])['postgres'])

    def test_missing_diff_and_full_modes_run_all(self):
        for mode in ['true', 'false']:
            result = subprocess.run([sys.executable, str(ROOT / 'scripts/ci/affected-ci-lanes.py'),
                                     '--range', 'not-a-real-reference..HEAD', '--selective', mode],
                                    cwd=ROOT, text=True, capture_output=True, check=True)
            self.assertNotIn('false', result.stdout)

    def test_effective_full_ci_forces_whole_solution_producer(self):
        ci = yaml.load((ROOT / '.github/workflows/ci.yml').read_text(), Loader=yaml.BaseLoader)
        step = next(step for step in ci['jobs']['build']['steps'] if step.get('id') == 'affected')
        self.assertEqual('${{ needs.changes.outputs.full_ci }}', step['env'].get('FULL_CI'))
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            scripts = root / 'scripts/ci'
            scripts.mkdir(parents=True)
            compute = scripts / 'compute-affected-projects.sh'
            compute.write_text('#!/bin/bash\n[[ "$FULL_CI" != true ]] || exit 99\necho tests/Example.csproj\n')
            compute.chmod(0o755)
            (scripts / 'protocol-profile-selection.py').write_text('print("{}")')
            for full_ci, expected in [('true', 'full'), ('false', 'affected')]:
                with self.subTest(full_ci=full_ci):
                    output = root / 'output'
                    output.write_text('')
                    env = dict(os.environ, FULL_CI=full_ci, RUNNER_TEMP=temporary, GITHUB_OUTPUT=str(output))
                    subprocess.run(['bash', '-e', '-c', step['run']], cwd=root, env=env,
                                   capture_output=True, text=True, check=True)
                    self.assertIn('mode=' + expected, output.read_text())

    def test_workflow_preserves_slow_first_build_reuse_and_full_fallback(self):
        ci = yaml.load((ROOT / '.github/workflows/ci.yml').read_text(), Loader=yaml.BaseLoader)
        jobs = ci['jobs']
        checkout = next(step for step in jobs['build']['steps'] if 'actions/checkout@' in step.get('uses', ''))
        self.assertEqual('0', checkout.get('with', {}).get('fetch-depth'))
        scripts = '\n'.join(step.get('run', '') for step in jobs['targeted-shards']['steps'])
        self.assertIn('sort_by(-.dispatch_rank)', scripts)
        self.assertIn('build', jobs['server-tests']['needs'])
        parallelism = jobs['server-tests']['strategy']['max-parallel']
        self.assertEqual('${{ inputs.selective_base && '
                         'fromJson(needs.targeted-shards.outputs.affected_max_parallel) || 16 }}', parallelism)
        self.assertNotIn('full_ci', parallelism,
                         'Full coverage must not expand an affected run beyond its runner budget')
        compute = next(step for step in jobs['targeted-shards']['steps'] if step.get('id') == 'compute')
        self.assertIn('python3 scripts/ci/trailing_selection.py)" >> "$GITHUB_OUTPUT"', compute['run'])
        self.assertIn('--schedule-shards', compute['run'])
        self.assertEqual(
            "${{ steps.compute.outputs.schedule_shards }}",
            jobs['targeted-shards']['outputs']['schedule_shards'])
        self.assertIn(
            "needs.targeted-shards.outputs.schedule_shards != 'false'",
            jobs['server-tests']['if'])
        self.assertEqual(
            "${{ steps.filter.outputs.core_package }}",
            jobs['changes']['outputs']['core_package'])
        self.assertIn(
            "needs.changes.outputs.core_package == 'true'",
            jobs['core-package-compatibility']['if'])
        step = next(step for step in jobs['changes']['steps'] if step.get('id') == 'specialized')
        self.assertIn("trailing_selective == 'true'", step['env']['SELECTIVE'])
        self.assertIn("full_ci != 'true'", step['env']['SELECTIVE'])
        for job in ['js-integration-tests', 'esri-leaflet-browser-tests', 'maplibre-compat',
                    'docker-build', 'python-integration-tests', 'mcp-certification', 'quickstart-console-smoke']:
            self.assertIn('inputs.selective_base &&', jobs[job]['if'])
            self.assertIn("full_ci == 'true'", jobs[job]['if'])

    def test_selective_smoke_skip_is_proved_and_selected_lanes_still_run(self):
        jobs = yaml.load((ROOT / '.github/workflows/ci.yml').read_text(), Loader=yaml.BaseLoader)['jobs']
        summary = next(step for step in jobs['test-all']['steps'] if step.get('name') == 'Check required test job results')
        receipts = next(step for step in jobs['test-all']['steps'] if step.get('name') == 'Verify server shard execution receipts')
        receipt_download = next(step for step in jobs['test-all']['steps'] if step.get('name') == 'Download server shard receipts')
        self.assertIn("schedule_shards != 'false'", receipt_download['if'])
        self.assertIn('no_shards_selected', receipts['env']['SELECT_REASON'])
        compute = next(step for step in jobs['targeted-shards']['steps'] if step.get('id') == 'compute')
        self.assertIn('needs.changes.outputs.full_ci', compute['run'])
        self.assertIn('SCHEDULE_SHARDS', summary['run'])
        self.assertIn('!= skipped', summary['run'])
        self.assertIn('!cancelled() && !failure()', jobs['aot-build']['if'])
        self.assertIn('!cancelled() && !failure()', jobs['mcp-certification']['if'])


class ShardWidthTests(unittest.TestCase):
    def affected_width(self, shards):
        result = subprocess.run(
            [sys.executable, str(ROOT / 'scripts/ci/trailing_selection.py')],
            input='[' + ','.join('{}' for _ in range(shards)) + ']',
            capture_output=True, text=True, check=True)
        return int(result.stdout)

    def test_narrow_affected_runs_keep_pr_safe_width(self):
        self.assertEqual(2, self.affected_width(1))
        self.assertEqual(2, self.affected_width(30))

    def test_broad_affected_runs_double_width_without_reaching_full(self):
        self.assertEqual(2, self.affected_width(31))
        self.assertEqual(2, self.affected_width(134))

    def test_selective_no_path_match_skips_default_smoke_shards(self):
        self.assertFalse(TRAILING.affected_run_schedules_shards("no_path_match"))
        self.assertTrue(TRAILING.affected_run_schedules_shards("targeted"))
        self.assertTrue(TRAILING.affected_run_schedules_shards("unmapped_source_change"))

    def test_core_package_skips_only_docs_workflow_ci_and_tests(self):
        measured = [
            ".github/workflows/compose-release-lifecycle.yml",
            ".github/workflows/nightly-container-build.yml",
            "docs/guides/deploy/choose-serving-image.md",
            "docs/guides/deploy/cloud-deployments.md",
            "scripts/ci/test-store-profile-workflows.py",
            "scripts/ci/test_immutable_container_tags.py",
            "tests/dotnet/Honua.Architecture.Tests/NightlyImageCertificationTests.cs",
        ]
        self.assertTrue(TRAILING.core_package_required([]))
        self.assertFalse(TRAILING.core_package_required(measured))
        self.assertTrue(TRAILING.core_package_required(measured + ["src/Honua.Core/Core/Honua.Core.csproj"]))
        self.assertTrue(TRAILING.core_package_required(["Directory.Build.props"]))
        self.assertTrue(TRAILING.core_package_required([".github/ci-shards.json"]))

    def test_repository_metadata_skips_build_and_core_package(self):
        # 44254098776 built, formatted and packed Core for .github/codeql/pr-paths.txt alone.
        metadata = [
            ".github/codeql/pr-paths.txt",
            ".github/ISSUE_TEMPLATE/bug.yml",
            ".github/CODEOWNERS",
            ".github/dependabot.yml",
            ".github/hygiene.json",
        ]
        self.assertFalse(TRAILING.core_package_required(metadata))
        for path in [".github/actions/setup-dotnet-ci/action.yml", ".github/test-projects.json",
                     ".github/codeql-extra.yml", ".github/CODEOWNERS.bak"]:
            with self.subTest(path=path):
                self.assertTrue(TRAILING.core_package_required([path]))
        ci = yaml.load((ROOT / '.github/workflows/ci.yml').read_text(), Loader=yaml.BaseLoader)
        run = ci['jobs']['changes']['steps']
        script = next(step['run'] for step in run if 'REPO_METADATA=' in step.get('run', ''))
        self.assertIn(f"REPO_METADATA='{TRAILING.REPO_METADATA}'", script)
        self.assertEqual(2, script.count('-e "^${REPO_METADATA}"'))


if __name__ == '__main__':
    unittest.main()
