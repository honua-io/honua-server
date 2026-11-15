#!/usr/bin/env python3
"""Exercise aggregate cancellation admission and the production shell verdict."""
import itertools
import os
from pathlib import Path
import subprocess
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]


class HygieneCancellationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        workflow = yaml.safe_load((ROOT / '.github/workflows/repo-hygiene.yml').read_text())
        cls.job = workflow['jobs']['repo-hygiene']

    def test_cancelled_workflow_or_dependency_does_not_run_aggregate(self):
        expression = self.job['if'].removeprefix('${{').removesuffix('}}').strip()
        expression = expression.replace('!cancelled()', 'not workflow_cancelled').replace(
            "!contains(needs.*.result, 'cancelled')", "'cancelled' not in results").replace('&&', 'and')
        for results in itertools.product(['success', 'failure', 'skipped', 'cancelled'], repeat=len(self.job['needs'])):
            for workflow_cancelled in [False, True]:
                with self.subTest(results=results, workflow_cancelled=workflow_cancelled):
                    admitted = eval(expression, {'__builtins__': {}}, dict(
                        results=results, workflow_cancelled=workflow_cancelled))
                    self.assertEqual(admitted, not workflow_cancelled and 'cancelled' not in results)

    def test_active_verdict_requires_all_successes(self):
        variables = list(self.job['steps'][0]['env'])
        for values in itertools.product(['success', 'failure', 'skipped', ''], repeat=len(variables)):
            with self.subTest(results=values):
                result = subprocess.run(['bash', '-e', '-c', self.job['steps'][0]['run']],
                    env=dict(os.environ, **dict(zip(variables, values))))
                self.assertEqual(result.returncode == 0, all(v == 'success' for v in values))

    def test_required_contract_stays_intact(self):
        self.assertEqual(self.job['name'], 'repo-hygiene / Repository hygiene')
        self.assertIn('evidence-paths', self.job['needs'])
        self.assertIn('shared-hygiene', self.job['needs'])
        self.assertEqual(len(self.job['steps'][0]['env']), len(self.job['needs']))


if __name__ == '__main__':
    unittest.main()
