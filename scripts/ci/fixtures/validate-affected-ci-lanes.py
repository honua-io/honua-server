#!/usr/bin/env python3
"""Guard expensive trailing lanes without changing selection or PR/full coverage."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location('affected_ci_lanes', ROOT / 'scripts/ci/affected-ci-lanes.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class AffectedLaneTests(unittest.TestCase):
    def test_unknown_shared_and_empty_changes_fail_closed(self):
        for paths in [[], ['unknown-input'], ['Directory.Build.props'],
                      ['src/Honua.Core/Core/Types.cs'], ['src/Honua.Hosting/Startup.cs'],
                      ['src/Honua.Server/Startup/JsonContextRegistration.cs'],
                      ['src/Honua.Server/EndpointRegistry.cs']]:
            with self.subTest(paths=paths):
                self.assertTrue(all(MODULE.select(paths).values()))

    def test_shared_setup_actions_keep_their_consumers(self):
        for path in ['.github/actions/setup-honua-server/action.yml',
                     '.github/actions/setup-honua-mcp/action.yml']:
            with self.subTest(path=path):
                self.assertTrue(all(MODULE.select([path]).values()))

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
            'aot': 'eng/aot/smoke.sh', 'docker': 'docker/Dockerfile',
            'postgres': 'src/Honua.Db/Postgres/Query.cs',
            'browser': 'tests/js-browser/maplibre.spec.ts',
            'python': 'tests/python/test_query.py', 'mcp': 'src/Honua.Ai/Features/Grounding/Tool.cs',
            'quickstart': 'scripts/docker/quickstart.sh',
        }
        for lane, path in inputs.items():
            with self.subTest(lane=lane):
                self.assertTrue(MODULE.select([path])[lane])

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

    def test_workflow_preserves_slow_first_build_reuse_and_full_fallback(self):
        ci = yaml.load((ROOT / '.github/workflows/ci.yml').read_text(), Loader=yaml.BaseLoader)
        jobs = ci['jobs']
        checkout = next(step for step in jobs['build']['steps'] if 'actions/checkout@' in step.get('uses', ''))
        self.assertEqual('0', checkout.get('with', {}).get('fetch-depth'))
        scripts = '\n'.join(step.get('run', '') for step in jobs['targeted-shards']['steps'])
        self.assertIn('sort_by(-.dispatch_rank)', scripts)
        self.assertIn('build', jobs['server-tests']['needs'])
        self.assertIn('6 || 24', jobs['server-tests']['strategy']['max-parallel'])
        self.assertIn("needs.changes.outputs.full_ci != 'true'",
                      jobs['server-tests']['strategy']['max-parallel'])
        step = next(step for step in jobs['changes']['steps'] if step.get('id') == 'specialized')
        self.assertIn("trailing_selective == 'true'", step['env']['SELECTIVE'])
        self.assertIn("full_ci != 'true'", step['env']['SELECTIVE'])
        for job in ['js-integration-tests', 'esri-leaflet-browser-tests', 'maplibre-compat',
                    'docker-build', 'python-integration-tests', 'mcp-certification', 'quickstart-console-smoke']:
            self.assertIn('inputs.selective_base &&', jobs[job]['if'])
            self.assertIn("full_ci == 'true'", jobs[job]['if'])


if __name__ == '__main__':
    unittest.main()
