"""Validate installed-authoring fixture journals without importing the source harness."""
import json
from datetime import datetime
from pathlib import Path
import re

SCHEMA = 'honua.gp.events/v1'
PHASE_LIMIT = 500
MESSAGE_LIMIT = 2000
PERCENT_LIMIT = 100


def validate_journal(receipt_path):
    """Check the documented v1 contract; unknown kinds may carry future payloads."""
    receipt_path = Path(receipt_path)
    receipt = json.loads(receipt_path.read_text(encoding='utf-8'))
    assert receipt['eventsSchema'] == SCHEMA, 'Incorrect receipt eventsSchema'
    assert receipt['events'] == 'events.jsonl', 'Expected the local events.jsonl journal'
    journal = receipt_path.parent / receipt['events']
    records = [json.loads(line) for line in journal.read_text(encoding='utf-8').splitlines()]
    assert records, 'Journal is empty'
    for seq, record in enumerate(records):
        assert isinstance(record, dict), f'Event {seq} is not an object'
        assert type(record['v']) is int and record['v'] == 1, 'Incorrect event version'
        assert type(record['seq']) is int and record['seq'] == seq, 'Noncontiguous event sequence'
        assert type(record['elapsedMs']) is int and record['elapsedMs'] >= 0, 'Invalid elapsedMs'
        assert isinstance(record['attempt'], str) and record['attempt'], 'Missing attempt'
        assert isinstance(record['kind'], str) and record['kind'], 'Missing kind'
        assert re.fullmatch(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z', record['at']), 'Invalid UTC timestamp'
        datetime.fromisoformat(record['at'].replace('Z', '+00:00'))
        validate_payload(record, receipt)
    validate_journal_order(records, receipt)
    assert records[-1]['kind'] == 'terminal', 'Missing terminal event'
    return records


def validate_payload(record, receipt):
    """Validate known payloads while allowing future event kinds."""
    kind = record['kind']
    if kind in ('phase', 'progress'):
        assert isinstance(record['phase'], str) and len(record['phase']) <= PHASE_LIMIT, 'Invalid phase'
    if kind == 'progress':
        assert type(record['percent']) in (int, float) and 0 <= record['percent'] <= PERCENT_LIMIT, 'Invalid percent'
    elif kind == 'log':
        assert record['level'] in ('info', 'warning'), 'Invalid log level'
        assert isinstance(record['message'], str) and len(record['message']) <= MESSAGE_LIMIT, 'Invalid log message'
    elif kind == 'artifact':
        artifact = record['artifact']
        assert isinstance(artifact['name'], str) and artifact['name'], 'Invalid artifact name'
        assert type(artifact['sizeBytes']) is int and artifact['sizeBytes'] >= 0, 'Invalid artifact size'
    elif kind == 'failure':
        assert all(isinstance(record['error'][key], str) for key in ('type', 'message')), 'Invalid error'
    elif kind == 'truncated':
        assert all(type(record[key]) is int and record[key] > 0 for key in ('dropped', 'maxEvents', 'maxBytes')), 'Invalid truncation disclosure'
    elif kind == 'terminal':
        assert record['status'] in ('succeeded', 'failed', 'setupFailed', 'cancelled'), 'Invalid terminal status'
        assert type(record['exitCode']) is int, 'Invalid terminal exit code'
        assert record['status'] == receipt['status'], 'Terminal status disagrees with receipt'
        assert record['exitCode'] == receipt['exitCode'], 'Terminal exit code disagrees with receipt'


def validate_journal_order(records, receipt):
    """Check measured time and truncation disclosure across the complete run."""
    elapsed = [record['elapsedMs'] for record in records]
    assert elapsed == sorted(elapsed), 'Decreasing elapsedMs'
    terminals = [index for index, record in enumerate(records) if record['kind'] == 'terminal']
    assert terminals == [len(records) - 1], 'Terminal event must be last'
    disclosures = [index for index, record in enumerate(records) if record['kind'] == 'truncated']
    assert type(receipt['eventsTruncated']) is bool, 'Invalid receipt truncation flag'
    assert type(receipt['eventsDropped']) is int and receipt['eventsDropped'] >= 0, 'Invalid receipt dropped count'
    if disclosures:
        assert disclosures == [len(records) - 2], 'Truncation disclosure must immediately precede terminal'
        assert receipt['eventsTruncated'], 'Missing receipt truncation flag'
        assert records[disclosures[0]]['dropped'] == receipt['eventsDropped'], 'Truncation count disagrees with receipt'
    else:
        assert not receipt['eventsTruncated'] and receipt['eventsDropped'] == 0, 'Missing truncation disclosure'
