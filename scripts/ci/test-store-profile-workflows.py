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
    def test_default_profile_verification_preserves_full_producer_and_runtime_proofs(self):
        jobs = workflow('pr-gate.yml')['jobs']
        producer_names = [step.get('name', '') for step in jobs['pr-gate']['steps']]
        verify_name = 'Verify default PostGIS store profile'
        self.assertNotIn(verify_name, producer_names)
        self.assertIn('Publish pristine managed test outputs', producer_names)
        self.assertIn('Honua.Server boot + migrations smoke', producer_names)
        consumer = jobs['pr-gate-tests']
        self.assertEqual(consumer['needs'], 'pr-gate')
        steps = consumer['steps']
        names = [step.get('name', '') for step in steps]
        verify = steps[names.index(verify_name)]
        self.assertGreater(names.index(verify_name), names.index('Test serving-image boundary detector'))
        self.assertEqual(steps[names.index('Setup .NET')]['with']['enable-build-cache'], 'false')
        self.assertIn('-p:HonuaStoreProfile=postgis', verify['run'])
        self.assertIn('StoreProfileWritePolicyTests', verify['run'])
        self.assertIn('RunConfiguration.TreatNoTestsAsError=true', verify['run'])
        self.assertEqual(verify['env']['HONUA_EXPECTED_STORE_PROFILE'], 'postgis')
        self.assertEqual(verify['if'], steps[names.index('Test serving-image boundary detector')]['if'])

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

    def run_gate(self, required, result):
        script = workflow('ci.yml')['jobs']['ci-gate']['steps'][0]['run']
        script = re.sub(r'\$\{\{.*?\}\}', 'false', script)
        environment = {**os.environ, 'REQUIRE_MANAGED_BUILD': 'false', 'REQUIRE_FORMAT': 'false',
                       'REQUIRE_STORE_IMAGES': required, 'STORE_IMAGE_RESULT': result}
        return subprocess.run(['bash'], input=script, env=environment, text=True, capture_output=True)


if __name__ == '__main__':
    unittest.main()
