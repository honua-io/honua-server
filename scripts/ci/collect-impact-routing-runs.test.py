#!/usr/bin/env python3
"""Exercise the collector against a seven-day, 1,400-run API replay."""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('collector', Path(__file__).with_name('collect-impact-routing-runs.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
start = module.timestamp('2026-09-01T00:00:00Z')
end = start + 7 * 86400 - 1
rows = [{'id': i + 1, 'created_at': module.iso(start + i * 400)} for i in range(1400)]
calls = []


def fetch(endpoint, parameters):
    assert 'status' not in parameters
    calls.append(parameters)
    lower, upper = map(module.timestamp, parameters['created'].split('..'))
    selected = list(reversed([row for row in rows if lower <= module.timestamp(row['created_at']) <= upper]))
    offset = (parameters['page'] - 1) * 100
    # Reproduce GitHub's filtered-list ceiling. A naive 14-page reader loses 400.
    return {'total_count': len(selected), 'workflow_runs': selected[:1000][offset:offset + 100]}


queries = module.collect('runs', start, end, 1000, fetch=fetch)
assert len(queries) == 2
assert len(calls) == 16  # root count plus 8 + 7 pages for uneven time slices
assert {row['id'] for query in queries for page in query for row in page['workflow_runs']} == set(range(1, 1401))
assert queries == module.collect('runs', start, end, 1000, fetch=fetch)

# A same-total shift that duplicates a page must be retried too.
raced = False
pauses = []

def race(endpoint, parameters):
    global raced
    page = fetch(endpoint, parameters)
    if parameters['page'] == 2 and not raced:
        raced = True
        return fetch(endpoint, {**parameters, 'page': 1})
    return page

assert module.collect('runs', start, end, 1000, fetch=race, pause=pauses.append) == queries
assert pauses == [5]

# A moved total discards the entire slice, including the first page.
raced = False

def moved(endpoint, parameters):
    global raced
    page = fetch(endpoint, parameters)
    if parameters['page'] == 2 and not raced:
        raced = True
        page['total_count'] += 1
    return page

assert module.collect('runs', start, end, 1000, fetch=moved, pause=lambda _: None) == queries

# No tolerance for persistent missing pages, overlapping IDs, or dense seconds.
for broken in (
    lambda endpoint, p: {**fetch(endpoint, p), 'workflow_runs': []},
    lambda endpoint, p: {**fetch(endpoint, p), 'workflow_runs': [{'id': 1, 'created_at': module.iso(start)}] * 100},
):
    try:
        module.collect('runs', start, end, 1000, fetch=broken, pause=lambda _: None)
    except ValueError:
        pass
    else:
        raise AssertionError('corrupt catalog accepted')
try:
    module.collect('runs', start, start, 1000, fetch=lambda *_: {'total_count': 1001, 'workflow_runs': []})
except ValueError as error:
    assert 'one second' in str(error)
else:
    raise AssertionError('unsplittable query accepted')
print('seven-day-catalog-replay=ok runs=1400 before=800-page-budget-failure after=1400/1400')
