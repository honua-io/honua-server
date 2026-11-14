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
        for results in itertools.product(['success', 'failure', 'skipped', 'cancelled'], repeat=2):
            for workflow_cancelled in [False, True]:
                with self.subTest(results=results, workflow_cancelled=workflow_cancelled):
                    admitted = eval(expression, {'__builtins__': {}}, dict(
                        results=results, workflow_cancelled=workflow_cancelled))
                    self.assertEqual(admitted, not workflow_cancelled and 'cancelled' not in results)

    def test_active_verdict_requires_both_successes(self):
        for evidence, shared in itertools.product(['success', 'failure', 'skipped', ''], repeat=2):
            with self.subTest(evidence=evidence, shared=shared):
                result = subprocess.run(['bash', '-e', '-c', self.job['steps'][0]['run']],
                    env=dict(os.environ, EVIDENCE_RESULT=evidence, SHARED_RESULT=shared))
                self.assertEqual(result.returncode == 0, evidence == shared == 'success')

    def test_required_contract_stays_intact(self):
        self.assertEqual(self.job['name'], 'repo-hygiene / Repository hygiene')
        self.assertEqual(self.job['needs'], ['evidence-paths', 'shared-hygiene'])


if __name__ == '__main__':
    unittest.main()
