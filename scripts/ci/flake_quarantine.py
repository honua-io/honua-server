#!/usr/bin/env python3
"""Validate bounded issue-backed quarantine and verify actual retry results."""
from collections import Counter, defaultdict, deque
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
MAX_QUARANTINED = 25
MAX_AGE = timedelta(days=14)
SAFE_TEST = re.compile(r'[A-Za-z_][A-Za-z0-9_.+]*\Z')
SCHEMA = 'honua.flake-quarantine/v1'


def catalog():
    return json.loads((ROOT / '.github/test-projects.json').read_text())['projects']


def validate_identity(entry):
    if entry.get('project') not in catalog() or not SAFE_TEST.fullmatch(entry.get('test', '')):
        raise ValueError('Quarantine requires a catalogued project and a literal test method')


def timestamp(value):
    parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
    if parsed.tzinfo is None or parsed.utcoffset() != timedelta(0):
        raise ValueError('Quarantine timestamps must be UTC')
    return parsed


def active_entries(entries, now=None):
    now = now or datetime.now(timezone.utc)
    active, seen = [], set()
    for entry in entries:
        validate_identity(entry)
        identity = entry['project'], entry['test']
        if identity in seen or type(entry.get('issue')) is not int or entry['issue'] < 1:
            raise ValueError('Duplicate quarantine or invalid issue reference')
        seen.add(identity)
        start, end = timestamp(entry['quarantined_at']), timestamp(entry['expires_at'])
        if start > now or end <= start or end - start > MAX_AGE:
            raise ValueError('Quarantine lifetime must be positive and at most fourteen days')
        if end > now:
            active.append(entry)
    if len(active) > MAX_QUARANTINED:
        raise ValueError('Quarantine exceeds the twenty-five test cap')
    return active


def load_snapshot(path):
    data = json.loads(Path(path).read_text())
    if data.get('schema') != SCHEMA or not isinstance(data.get('entries'), list):
        raise ValueError('Unknown quarantine snapshot schema')
    return active_entries(data['entries'])


def exclude_filter(expression, entries, project):
    for entry in entries:
        if entry['project'] == project:
            exclusion = f'FullyQualifiedName!={entry["test"]}'
            expression = f'({expression})&{exclusion}' if expression else exclusion
    return expression


def read_cases(path):
    tree = ET.parse(path)
    methods = {}
    for definition in tree.findall('.//{*}TestDefinitions/{*}UnitTest'):
        method = definition.find('{*}TestMethod')
        if method is not None:
            methods[definition.attrib['id']] = method.attrib['className'] + '.' + method.attrib['name']
    cases = []
    for row in tree.findall('.//{*}UnitTestResult'):
        method = methods.get(row.attrib.get('testId'))
        if not method or not SAFE_TEST.fullmatch(method):
            raise ValueError('TRX does not identify a literal test method')
        if row.attrib.get('outcome') not in ('Passed', 'Failed', 'NotExecuted'):
            raise ValueError('Unexpected test outcome')
        cases.append((method, row.attrib['testName'], row.attrib['outcome'], row))
    if not cases:
        raise ValueError('No executed result rows')
    validate_run_summary(tree, Counter(case[2] for case in cases))
    return cases


def validate_run_summary(tree, totals):
    expected = dict(total=sum(totals.values()), executed=sum(totals.values()) - totals['NotExecuted'],
                    passed=totals['Passed'], failed=totals['Failed'])
    counters = tree.find('.//{*}ResultSummary/{*}Counters')
    if counters is None or any(int(counters.attrib[key]) != value for key, value in expected.items()):
        raise ValueError('TRX counters disagree with result rows')
    for key in ('error', 'timeout', 'aborted', 'disconnected', 'notRunnable'):
        if int(counters.attrib.get(key, '0')) != 0:
            raise ValueError('TRX contains infrastructure failures')
    for info in tree.findall('.//{*}ResultSummary/{*}RunInfos/{*}RunInfo'):
        text = info.findtext('{*}Text', default='').strip()
        ordinary_failure = (totals['Failed'] > 0 and text.startswith('[xUnit.net ')
                            and text.endswith('[FAIL]') and '\n' not in text)
        if info.attrib.get('outcome') not in ('Warning', 'Informational') and not ordinary_failure:
            raise ValueError('TRX contains run-level failures')
    if tree.findall('.//{*}ResultSummary/{*}Output/{*}ErrorInfo'):
        raise ValueError('TRX contains run-level failures')


def retry_replacements(first, retry):
    failed = [c for c in read_cases(first) if c[2] == 'Failed']
    retried = read_cases(retry)
    methods = {c[0] for c in failed}
    if not failed or any(c[0] not in methods or c[2] != 'Passed' for c in retried):
        raise ValueError('Retry must pass every selected method')
    required = Counter((c[0], c[1]) for c in failed)
    actual = Counter((c[0], c[1]) for c in retried)
    if required - actual:
        raise ValueError('Retry omitted originally failed cases')
    replacements = defaultdict(deque)
    for method, name, _, row in retried:
        replacements[(method, name)].append(row)
    return [replacements[(c[0], c[1])].popleft() for c in failed]


def merge_retry(first, retry, output):
    replacements = iter(retry_replacements(first, retry))
    tree = ET.parse(first)
    rows = tree.find('{*}Results')
    for index, row in enumerate(list(rows)):
        if row.attrib.get('outcome') == 'Failed':
            replacement = next(replacements)
            # Preserve the original definition/entry identity while retaining actual retry evidence.
            replacement.set('testId', row.attrib['testId'])
            if 'executionId' in row.attrib:
                replacement.set('executionId', row.attrib['executionId'])
            rows.remove(row)
            rows.insert(index, replacement)
    outcomes = Counter(row.attrib['outcome'] for row in rows)
    counters = tree.find('.//{*}ResultSummary/{*}Counters')
    for key, value in dict(total=len(rows), executed=len(rows)-outcomes['NotExecuted'],
                           passed=outcomes['Passed'], failed=outcomes['Failed']).items():
        counters.set(key, str(value))
    summary = tree.find('{*}ResultSummary')
    summary.set('outcome', 'Completed')
    tree.write(output, encoding='utf-8', xml_declaration=True)
