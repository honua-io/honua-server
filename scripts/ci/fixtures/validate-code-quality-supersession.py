#!/usr/bin/env python3
"""Keep the privileged cancellation helper isolated from pull-request code."""
from pathlib import Path
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]


class TrustedSupersessionTests(unittest.TestCase):
    def test_trusted_events_and_checkout(self):
        workflow = yaml.safe_load((ROOT / '.github/workflows/code-quality-supersession.yml').read_text())
        events = workflow.get('on', workflow.get(True))
        self.assertEqual(set(events), {'pull_request_target', 'workflow_run'})
        self.assertEqual(events['workflow_run']['workflows'], ['CodeQL', 'Code Quality: PR #*'])
        self.assertEqual(events['workflow_run']['types'], ['requested', 'in_progress'])
        job = workflow['jobs']['cancel-superseded']
        self.assertIn("workflow_run.path == 'dynamic/github-code-quality/codeql'", job['if'])
        self.assertTrue(job['concurrency']['cancel-in-progress'])
        self.assertIn('pull_request.number', job['concurrency']['group'])
        self.assertIn('workflow_run.head_branch', job['concurrency']['group'])
        self.assertNotIn('concurrency', workflow)
        checkout, cancel = job['steps']
        self.assertEqual(checkout['with'], {
            'ref': '${{ github.event.repository.default_branch }}', 'persist-credentials': False})
        self.assertTrue(checkout['uses'].startswith('actions/checkout@'))
        self.assertTrue(cancel['uses'].startswith('actions/github-script@'))
        self.assertNotIn('github.event', cancel['with']['script'])
        self.assertEqual(workflow['permissions'], {
            'actions': 'write', 'contents': 'read', 'pull-requests': 'read'})


if __name__ == '__main__':
    unittest.main()
