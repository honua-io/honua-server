"""Regression checks for the checkout-free installed-authoring contract validator."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

from gp_installed_events import SCHEMA, validate_journal


class InstalledEventTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.receipt = {'eventsSchema': SCHEMA, 'events': 'events.jsonl',
                        'status': 'succeeded', 'exitCode': 0,
                        'eventsTruncated': False, 'eventsDropped': 0}
        self.records = [
            self.event('phase', phase='setup'),
            self.event('progress', phase='execute', percent=50),
            self.event('log', level='info', message='working'),
            self.event('artifact', artifact={'name': 'summary.json', 'sizeBytes': 42}),
            self.event('terminal', status='succeeded', exitCode=0),
        ]

    @staticmethod
    def event(kind, **fields):
        return {'v': 1, 'at': '2026-11-14T18:00:00.123Z', 'elapsedMs': 10,
                'attempt': 'local', 'kind': kind, **fields}

    def validate(self):
        for seq, record in enumerate(self.records):
            record['seq'] = seq
        (self.root / 'receipt.json').write_text(json.dumps(self.receipt), encoding='utf-8')
        (self.root / 'events.jsonl').write_text(
            ''.join(json.dumps(record) + '\n' for record in self.records), encoding='utf-8')
        return validate_journal(self.root / 'receipt.json')

    def test_success_journal(self):
        self.assertEqual(5, len(self.validate()))

    def test_unknown_kind_and_extra_fields_preserve_later_terminal(self):
        self.records.insert(2, self.event('future-diagnostic', extension={'arbitrary': [1, 2]}))
        self.records[0]['futureField'] = True
        self.assertEqual('terminal', self.validate()[-1]['kind'])

    def test_failure_and_truncation_journal(self):
        self.records.insert(-1, self.event('failure', error={'type': 'ToolError', 'message': 'failed'}))
        self.records.insert(-1, self.event('truncated', dropped=3, maxEvents=1000, maxBytes=262144))
        self.records[-1].update(status='failed', exitCode=1)
        self.receipt.update(status='failed', exitCode=1, eventsTruncated=True, eventsDropped=3)
        self.validate()

    def test_decreasing_elapsed_time_is_rejected(self):
        self.records[0]['elapsedMs'] = 100
        with self.assertRaisesRegex(AssertionError, 'Decreasing'):
            self.validate()

    def test_inconsistent_truncation_is_rejected(self):
        disclosure = self.event('truncated', dropped=3, maxEvents=1000, maxBytes=262144)
        self.records.insert(-1, disclosure)
        with self.assertRaisesRegex(AssertionError, 'flag'):
            self.validate()
        self.receipt.update(eventsTruncated=True, eventsDropped=2)
        with self.assertRaisesRegex(AssertionError, 'count'):
            self.validate()
        self.receipt['eventsDropped'] = 3
        self.records.insert(0, self.records.pop(-2))
        with self.assertRaisesRegex(AssertionError, 'immediately'):
            self.validate()
        self.records.insert(-1, disclosure)
        with self.assertRaisesRegex(AssertionError, 'immediately'):
            self.validate()

    def test_missing_truncation_disclosure_is_rejected(self):
        self.receipt.update(eventsTruncated=True, eventsDropped=3)
        with self.assertRaisesRegex(AssertionError, 'Missing truncation'):
            self.validate()

    def test_invalid_known_fields_are_rejected(self):
        cases = [(0, 'v', 2), (0, 'v', True), (0, 'elapsedMs', -1),
                 (0, 'elapsedMs', True), (0, 'attempt', ''), (0, 'kind', ''),
                 (0, 'at', '2026-11-14T18:00:00+00:00'),
                 (0, 'at', '2026-99-14T18:00:00.123Z'),
                 (0, 'phase', 'x' * 501), (1, 'percent', 101),
                 (1, 'percent', True), (2, 'level', 'debug'),
                 (2, 'message', 'x' * 2001),
                 (3, 'artifact', {'name': 'x', 'sizeBytes': -1}),
                 (4, 'status', 'failed'), (4, 'exitCode', 1)]
        original = copy.deepcopy(self.records)
        for index, field, value in cases:
            with self.subTest(field=field, value=value):
                self.records = copy.deepcopy(original)
                self.records[index][field] = value
                with self.assertRaises((AssertionError, ValueError)):
                    self.validate()

    def test_receipt_schema_and_reference_are_checked(self):
        for key, value in [('eventsSchema', 'honua.gp.events/v2'), ('events', 'missing.jsonl')]:
            with self.subTest(key=key):
                original = self.receipt[key]
                self.receipt[key] = value
                with self.assertRaises(AssertionError):
                    self.validate()
                self.receipt[key] = original

    def test_missing_and_early_terminal_are_rejected(self):
        terminal = self.records.pop()
        with self.assertRaises(AssertionError):
            self.validate()
        self.records.insert(0, terminal)
        with self.assertRaises(AssertionError):
            self.validate()

    def test_noncontiguous_sequence_is_rejected(self):
        self.validate()
        path = self.root / 'events.jsonl'
        records = json.loads('[' + ','.join(path.read_text().splitlines()) + ']')
        records[1]['seq'] = 7
        path.write_text('\n'.join(map(json.dumps, records)), encoding='utf-8')
        with self.assertRaisesRegex(AssertionError, 'Noncontiguous'):
            validate_journal(self.root / 'receipt.json')

    def test_empty_or_malformed_journal_is_rejected(self):
        self.validate()
        path = self.root / 'events.jsonl'
        for content in ('', '{broken}\n', '[]\n'):
            with self.subTest(content=content):
                path.write_text(content, encoding='utf-8')
                with self.assertRaises((AssertionError, ValueError)):
                    validate_journal(self.root / 'receipt.json')


if __name__ == '__main__':
    unittest.main()
