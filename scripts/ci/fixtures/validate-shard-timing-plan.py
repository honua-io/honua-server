#!/usr/bin/env python3
"""Offline scheduling/proposal regressions; no SDK, containers or GitHub calls."""
import importlib.util
import json
from datetime import datetime, timezone, timedelta
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('timing_plan', ROOT / 'scripts/ci/shard-timing-plan.py')
plan = importlib.util.module_from_spec(spec)
spec.loader.exec_module(plan)
NOW = datetime(2026, 10, 13, tzinfo=timezone.utc)
PROJECT = 'tests/Example/Example.csproj'


def config():
    return {'shards': [
        {'shard_name': 'A', 'csproj': PROJECT, 'filter': 'FullyQualifiedName~Example.Heavy'},
        {'shard_name': 'B', 'csproj': PROJECT, 'filter': 'FullyQualifiedName~Example.Light'}]}


def timing(shard, index=0, seconds=90, **overrides):
    source = next(s for s in config()['shards'] if s['shard_name'] == shard)
    record = {'shard': shard, 'status': 'passed', 'exit_code': 0, 'timed_out': False,
              'capacity_status': 'ok', 'duration_seconds': seconds,
              'started_at': (NOW - timedelta(days=index + 1)).isoformat(),
              'filter': f"(({source['filter']})&Tier!=Slow)&Tier!=Fast"}
    record.update(overrides)
    return record


def trx(path, classes, outcome='Passed'):
    ns = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
    def node(parent, tag, **attrs):
        return ET.SubElement(parent, '{' + ns + '}' + tag, attrs)
    root = ET.Element('{' + ns + '}TestRun')
    definitions = node(root, 'TestDefinitions')
    results = node(root, 'Results')
    for index, (name, seconds) in enumerate(classes.items()):
        test = node(definitions, 'UnitTest', id=str(index))
        node(test, 'TestMethod', className=name + ', Example', name='Method')
        node(results, 'UnitTestResult', testId=str(index), outcome=outcome,
             testName='Custom display name', duration=f'00:00:{seconds:06.3f}')
    summary = node(root, 'ResultSummary')
    node(summary, 'Counters', total=str(len(classes)), executed=str(len(classes)),
         passed=str(len(classes) if outcome == 'Passed' else 0), failed='0')
    ET.ElementTree(root).write(path)


class SchedulingTests(unittest.TestCase):
    def test_longest_first_preserves_selection_and_stable_ties(self):
        samples = {'A': [{'seconds': 10}], 'B': [{'seconds': 90}]}
        self.assertEqual(plan.order_names(['A', 'B'], config(), samples), ['B', 'A'])
        self.assertEqual(plan.order_names(['A', 'B'], config(), {'A': [{'seconds': 9}], 'B': [{'seconds': 9}]}), ['A', 'B'])
        self.assertEqual(plan.order_names(['A', 'B'], config(), {'A': [{'seconds': 90}]}), ['A', 'B'])
        for names in (['A', 'A'], ['Unknown']):
            with self.assertRaises(ValueError):
                plan.order_names(names, config(), samples)

    def test_reject_failed_stale_future_changed_filter_project_and_bad_values(self):
        variants = [dict(status='failed'), dict(exit_code=1), dict(timed_out=True),
                    dict(capacity_status='hang_suspected'), dict(filter='FullyQualifiedName~Other'),
                    dict(project='foreign.csproj'), dict(duration_seconds=-1),
                    dict(duration_seconds=10**1000), dict(duration_seconds=float('nan')), dict(duration_seconds=True),
                    dict(started_at=(NOW - timedelta(days=31)).isoformat()),
                    dict(started_at=(NOW + timedelta(days=1)).isoformat()), dict(started_at='bad')]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for i, fields in enumerate(variants):
                (root / f'{i}.timing.json').write_text(json.dumps(timing('A', **fields)))
            self.assertEqual(dict(plan.observations(config(), root, now=NOW)), {})

    def test_copies_do_not_inflate_history_and_only_latest_ten_count(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for i in range(12):
                for duplicate in range(2):
                    (root / f'{i}-{duplicate}.timing.json').write_text(json.dumps(timing('A', index=i, seconds=i + 1)))
            records = plan.observations(config(), root, now=NOW)['A']
            self.assertEqual(len(records), 10)
            self.assertEqual({r['seconds'] for r in records}, set(range(1, 11)))

    def test_equivalent_parentheses_and_missing_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'one.timing.json').write_text(json.dumps(timing('A', filter='FullyQualifiedName~Example.Heavy&Tier!=Fast&Tier!=Slow')))
            self.assertEqual(len(plan.observations(config(), root, now=NOW)['A']), 1)
            self.assertEqual(dict(plan.observations(config(), root / 'missing', now=NOW)), {})

    def test_real_unit_component_and_legacy_full_selection_can_reorder(self):
        registry = json.loads((ROOT / '.github/ci-shards.json').read_text())
        catalog = json.loads((ROOT / '.github/test-projects.json').read_text())
        selected = [next(shard for shard in registry['shards']
                         if catalog['projects'][shard.get('csproj') or plan.MONOLITH]['role'] == role)
                    for role in ('unit', 'component', 'mixed')]
        cfg = {'shards': selected}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for index, shard in enumerate(selected):
                effective = '(' + shard['filter'] + ')&Tier!=Slow'
                if index == 2:
                    effective = '(' + effective + ')&Tier!=Fast'
                record = timing('A', seconds=(index + 1) * 10, filter=effective,
                                started_at=datetime.now(timezone.utc).isoformat(),
                                project=shard.get('csproj') or plan.MONOLITH)
                record['shard'] = shard['shard_name']
                (root / f'{index}.timing.json').write_text(json.dumps(record))
            samples = plan.observations(cfg, root)
            names = [shard['shard_name'] for shard in selected]
            self.assertEqual(set(samples), set(names))
            self.assertEqual(plan.order_names(names, cfg, samples), names[::-1])
            # Exact CLI behavior also consumes the real catalog; a unit receipt
            # must not be silently discarded before ordering the full selection.
            file = root / 'shards.json'
            file.write_text(json.dumps(cfg))
            result = subprocess.run([sys.executable, str(ROOT / 'scripts/ci/shard-timing-plan.py'),
                                     'order', '--config', str(file), '--timings-dir', str(root)],
                                    input='\n'.join(names) + '\n', capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout.splitlines(), names[::-1])

    def test_role_specific_bad_filters_rejected_and_unknown_role_is_legacy(self):
        for role in ('unit', 'component', 'mixed', 'unknown', None):
            catalog = {'projects': {PROJECT: {'role': role}}} if role else {'projects': {}}
            include_fast = role in ('unit', 'component')
            correct = 'FullyQualifiedName~Example.Heavy&Tier!=Slow'
            incorrect = correct + '&Tier!=Fast'
            if not include_fast:
                correct, incorrect = incorrect, correct
            with self.subTest(role=role), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                (root / 'good.timing.json').write_text(json.dumps(timing('A', filter=correct)))
                (root / 'wrong-tier.timing.json').write_text(json.dumps(timing('A', index=1, filter=incorrect)))
                (root / 'wrong-selection.timing.json').write_text(json.dumps(timing('A', index=2, filter=correct + '&FullyQualifiedName~Foreign')))
                (root / 'wrong-slow.timing.json').write_text(json.dumps(timing('A', index=3, filter=correct.replace('Tier!=Slow', 'Tier=Fast'))))
                samples = plan.observations(config(), root, now=NOW, catalog=catalog)
                self.assertEqual(len(samples['A']), 1)
                self.assertTrue(samples['A'][0]['timing'].endswith('good.timing.json'))

    def test_cli_fallback_and_reject_unknown(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cfg = root / 'config.json'
            cfg.write_text(json.dumps(config()))
            command = [sys.executable, str(ROOT / 'scripts/ci/shard-timing-plan.py'), 'order', '--config', str(cfg), '--timings-dir', str(root)]
            result = subprocess.run(command, input='B\nA\n', text=True, capture_output=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout, 'B\nA\n')
            self.assertNotEqual(subprocess.run(command, input='Unknown\n', text=True, capture_output=True).returncode, 0)


class RecommendationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        for name, classes in [('A', {'Example.Heavy1': 90, 'Example.Heavy2': 30}), ('B', {'Example.Light1': 10, 'Example.Light2': 10})]:
            for i in range(3):
                path = self.root / f'{name}-{i}.timing.json'
                path.write_text(json.dumps(timing(name, index=i, seconds=sum(classes.values()))))
                trx(path.with_suffix('').with_suffix('.trx'), classes)
        self.samples = plan.observations(config(), self.root, now=NOW)
        self.groups = {'schema': 'honua.shard-balance-groups/v1', 'routingFingerprint': plan.fingerprint(config()),
                       'projects': {PROJECT: [{'name': 'Heavy1', 'classes': ['Example.Heavy1']},
                                              {'name': 'Heavy2', 'classes': ['Example.Heavy2']},
                                              {'name': 'SharedCollection', 'classes': ['Example.Light1', 'Example.Light2']}]}}

    def test_collection_is_atomic_no_new_shards_and_no_config_mutation(self):
        cfg = config()
        before = json.dumps(cfg)
        report = plan.recommend(cfg, self.samples, self.groups)
        self.assertEqual(report['gaps'], [])
        proposal = report['proposals'][0]
        self.assertEqual(proposal['shardCount'], 2)
        self.assertTrue(proposal['improvesSummedBalance'])
        collection = next(a for a in proposal['assignments'] if a['group'] == 'SharedCollection')
        self.assertEqual(collection['classes'], ['Example.Light1', 'Example.Light2'])
        self.assertEqual(before, json.dumps(cfg))

    def test_insufficient_history_or_missing_groups_is_gap(self):
        for samples, groups in [({}, self.groups), (self.samples, None)]:
            report = plan.recommend(config(), samples, groups)
            self.assertEqual(report['proposals'], [])
            self.assertTrue(report['gaps'])

    def test_stale_routing_group_map_rejected(self):
        self.groups['routingFingerprint'] = 'old'
        with self.assertRaises(ValueError):
            plan.recommend(config(), self.samples, self.groups)

    def test_missing_duplicate_or_unknown_class_rejected(self):
        for classes in [[], ['Example.Heavy1', 'Example.Heavy1'], ['Foreign.Type']]:
            with self.subTest(classes=classes):
                groups = json.loads(json.dumps(self.groups))
                groups['projects'][PROJECT][0]['classes'] = classes
                report = plan.recommend(config(), self.samples, groups)
                self.assertEqual(report['proposals'], [])
                self.assertTrue(report['gaps'])

    def test_incomplete_trx_or_changing_inventory_is_not_evidence(self):
        path = self.samples['A'][0]['trx']
        for classes, outcome in [({'Example.Heavy1': 90}, 'Passed'), ({'Example.Heavy1': 90}, 'NotExecuted')]:
            trx(path, classes, outcome)
            self.assertEqual(plan.recommend(config(), self.samples, self.groups)['proposals'], [])

    def test_custom_display_names_use_definition_identity(self):
        self.assertEqual(plan.class_times(self.samples['A'][0]['trx']), {'Example.Heavy1': 90, 'Example.Heavy2': 30})

    def test_counter_mismatch_rejects_partial_results(self):
        path = self.samples['A'][0]['trx']
        text = path.read_text().replace('total="2"', 'total="3"')
        path.write_text(text)
        with self.assertRaises(ValueError):
            plan.class_times(path)


if __name__ == '__main__':
    unittest.main()
