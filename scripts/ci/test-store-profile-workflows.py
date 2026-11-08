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
    def test_default_profile_compile_preserves_published_full_profile_artifacts_and_boot_proof(self):
        steps = workflow('pr-gate.yml')['jobs']['pr-gate']['steps']
        names = [step.get('name', '') for step in steps]
        compile_name = 'Compile default PostGIS store profile'
        self.assertIn(compile_name, names)
        compile_step = steps[names.index(compile_name)]
        self.assertGreater(names.index(compile_name), names.index('Publish pristine managed test outputs'))
        self.assertGreater(names.index(compile_name), names.index('Honua.Server boot + migrations smoke'))
        self.assertIn('-p:HonuaStoreProfile=postgis', compile_step['run'])
        self.assertEqual(compile_step['if'], steps[names.index('Honua.Server boot + migrations smoke')]['if'])

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
