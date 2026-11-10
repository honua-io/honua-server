#!/usr/bin/env python3
"""Fail-closed affected fast-test selection fixtures; no managed compilation."""
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / 'compute-fast-test-selection.py'
spec = importlib.util.spec_from_file_location('fast_selection', SCRIPT)
selection = importlib.util.module_from_spec(spec)
spec.loader.exec_module(selection)


class SelectionTests(unittest.TestCase):
    def setUp(self):
        self.roots = {group: [group + '.csproj'] for group in selection.GROUPS}

    def test_empty_and_unknown_closures(self):
        self.assertEqual([], selection.select([], self.roots))
        for closure in [None, ['ALL']]:
            self.assertEqual(list(selection.GROUPS), selection.select(closure, self.roots))

    def test_reverse_closure_selects_consuming_groups(self):
        self.assertEqual(['architecture', 'unit-fast', 'server-governance'],
                         selection.select(['library.csproj', 'unit-fast.csproj'], self.roots))
        self.assertEqual(['architecture', 'server-fast', 'server-governance'],
                         selection.select(['server-fast.csproj', 'server-governance.csproj'], self.roots))
        self.assertEqual(['architecture', 'server-governance'], selection.select(['tool.csproj'], self.roots))

    def test_integration_closure_retains_repository_governance(self):
        groups = selection.select(
            ['tests/dotnet/Honua.Protocols.OgcApi.Tests/Honua.Protocols.OgcApi.Tests.csproj'], self.roots)
        self.assertIn('server-governance', groups)
        self.assertIn('architecture', groups)

    def test_plan_uses_shared_closure_and_existing_group_roots(self):
        members = '\n'.join(value[0] for value in self.roots.values())
        outputs = [members] + [value[0] for value in self.roots.values()]
        with patch.dict(os.environ, GITHUB_EVENT_NAME='pull_request'), \
                patch.object(selection.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, 'unit-fast.csproj\n')) as affected, \
                patch.object(selection.subprocess, 'check_output', side_effect=outputs) as roots, \
                patch.object(selection, 'safe_selection', return_value=None):
            self.assertEqual(['architecture', 'unit-fast', 'server-governance'], selection.plan())
        self.assertEqual('HEAD^1', affected.call_args.kwargs['env']['BASE_REF'])
        self.assertIn('compute-affected-projects.sh', affected.call_args.args[0][1])
        self.assertEqual(list(selection.GROUPS), [call.kwargs['env']['GATE_TEST_GROUP'] for call in roots.call_args_list[1:]])

    def test_non_pr_events_retain_every_group(self):
        with patch.dict(os.environ, GITHUB_EVENT_NAME='workflow_dispatch'), \
                patch.object(selection.subprocess, 'run') as execute:
            self.assertEqual(list(selection.GROUPS), selection.plan())
        execute.assert_not_called()

    def test_invalid_closure_and_roots_fail_closed(self):
        for closure, members, roots in [('unknown.csproj', 'known.csproj', []),
                                         ('known.csproj', 'known.csproj', [''])]:
            with self.subTest(closure=closure, roots=roots), \
                    patch.dict(os.environ, GITHUB_EVENT_NAME='pull_request'), \
                    patch.object(selection.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, closure)), \
                    patch.object(selection.subprocess, 'check_output', side_effect=[members, *roots]), \
                    patch.object(selection, 'safe_selection', return_value=None):
                with self.assertRaises(ValueError):
                    selection.plan()

    def test_unmapped_inputs_require_positive_docs_proof(self):
        for verdict, expected in [('docs_only=false', list(selection.GROUPS)), ('docs_only=true', [])]:
            with patch.object(selection.subprocess, 'check_output', return_value='docs/internal/developer/metadata-catalog-endpoints.v1.json\0'), \
                    patch.object(selection.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, verdict)):
                self.assertEqual(expected, selection.safe_selection([], set()))

    def test_mixed_governed_input_retains_all_groups(self):
        changed = 'tests/dotnet/Honua.Core.Security.Tests/DataConnectionTests.cs\0certification/cite-protocol-requirements.v1.json\0'
        members = {'tests/dotnet/Honua.Core.Security.Tests/Honua.Core.Security.Tests.csproj'}
        with patch.object(selection.subprocess, 'check_output', return_value=changed), \
                patch.object(selection, 'linked_inputs', return_value=False), \
                patch.object(selection, 'docs_proven', return_value=False) as proof:
            self.assertEqual(list(selection.GROUPS), selection.safe_selection(list(members), members))
            proof.assert_called_once_with(['certification/cite-protocol-requirements.v1.json'])

    def test_linked_sources_retain_every_group(self):
        cases = ['tests/dotnet/Shared/ProtocolUnits/Ai/McpTaxonomyAlignmentTests.Shared.cs',
                 'tests/dotnet/Honua.Server.Tests/Features/NlQuery/NlQueryTestResources.cs']
        members = {'tests/dotnet/Honua.Ai.UnitTests/Honua.Ai.UnitTests.csproj'}
        for changed in cases:
            with self.subTest(changed=changed), \
                    patch.object(selection.subprocess, 'check_output', return_value=changed + '\0'):
                self.assertTrue(selection.linked_inputs([changed], members))
                self.assertEqual(list(selection.GROUPS), selection.safe_selection(['known.csproj'], members))

    def test_errors_emit_all_groups(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'outputs'
            with patch.dict(os.environ, GITHUB_OUTPUT=str(output)), \
                    patch('sys.argv', [str(SCRIPT), '--github-output']), \
                    patch('sys.stdout', new_callable=io.StringIO), \
                    patch.object(selection, 'plan', side_effect=ValueError('untrusted closure')):
                selection.main()
            self.assertEqual([group + '=true' for group in selection.GROUPS], output.read_text().splitlines())


if __name__ == '__main__':
    unittest.main()
