#!/usr/bin/env python3
"""A successful sibling must never mask an empty, failed or timed-out assembly."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

SCRIPT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('multi', SCRIPT / 'run-multi-project-shard.py')
multi = importlib.util.module_from_spec(spec)
spec.loader.exec_module(multi)


class MultiProjectTests(unittest.TestCase):
    def run_group(self, outcomes):
        config = json.loads(Path('.github/ci-shards.json').read_text())
        projects = next(s['csprojs'] for s in config['shards'] if 'csprojs' in s)[:2]
        with tempfile.TemporaryDirectory() as directory:
            environment = {**os.environ, 'HONUA_SERVER_TEST_CSPROJS': json.dumps(projects),
                           'HONUA_SERVER_TEST_SHARD_NAME': 'group', 'HONUA_SERVER_TEST_LOG_NAME': 'group',
                           'HONUA_SERVER_TEST_FILTER': 'FullyQualifiedName~Honua',
                           'HONUA_SERVER_TEST_TIMEOUT_MINUTES': '20',
                           'HONUA_SERVER_TEST_RESULTS_DIR': directory}
            def execute(child, remaining):
                index = projects.index(child['HONUA_SERVER_TEST_CSPROJ'])
                outcome = outcomes[index]
                root = ET.Element('TestRun')
                results = ET.SubElement(root, 'Results')
                if outcome != 'empty':
                    ET.SubElement(results, 'UnitTestResult', outcome=outcome)
                counts = {'total': '0' if outcome == 'empty' else '1',
                          'executed': '0' if outcome == 'empty' else '1',
                          'passed': '1' if outcome == 'Passed' else '0',
                          'failed': '1' if outcome == 'Failed' else '0'}
                ET.SubElement(ET.SubElement(root, 'ResultSummary'), 'Counters', **counts)
                path = Path(child['HONUA_SERVER_TEST_RESULTS_DIR']) / child['HONUA_SERVER_TEST_LOG_NAME']
                ET.ElementTree(root).write(path.with_suffix('.trx'))
                path.with_suffix('.timing.json').write_text(json.dumps({
                    'status': 'passed' if outcome == 'Passed' else 'failed'}))
                return 0 if outcome in ('Passed', 'empty') else 1
            with patch.dict(os.environ, environment, clear=True), patch.object(multi, 'execute', side_effect=execute):
                code = multi.main()
            record = json.loads((Path(directory) / 'group.timing.json').read_text())
            return code, record

    def test_all_projects_contribute_to_execution_evidence(self):
        code, record = self.run_group(['Passed', 'Passed'])
        self.assertEqual(0, code)
        self.assertEqual(2, record['execution']['counts']['executed'])
        self.assertEqual('passed', record['status'])

    def test_success_cannot_mask_failed_or_empty_sibling(self):
        for outcomes in (['Passed', 'Failed'], ['Failed', 'Passed'], ['Passed', 'empty']):
            with self.subTest(outcomes=outcomes):
                code, record = self.run_group(outcomes)
                self.assertNotEqual(0, code)
                self.assertEqual('failed', record['status'])

    def test_timeout_stops_group_and_removes_stale_sibling_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            stale = directory / 'project-1.trx'
            stale.write_text('stale passing results')
            with patch.object(multi, 'execute', return_value=124):
                attempts, code = multi.run_projects(['a.csproj', 'b.csproj'],
                    {'HONUA_SERVER_TEST_TIMEOUT_MINUTES': '1'}, directory)
            self.assertEqual(124, code)
            self.assertEqual(1, len(attempts))
            self.assertFalse(stale.exists())


if __name__ == '__main__':
    unittest.main()
