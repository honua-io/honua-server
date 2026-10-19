#!/usr/bin/env python3
"""Protect the measured Admin Runtime cut without changing its runnable test surface."""
import copy
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('coverage', ROOT / 'scripts/ci/check-server-test-shard-coverage.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
CONFIG = json.loads((ROOT / '.github/ci-shards.json').read_text())
EVIDENCE = json.loads((ROOT / 'docs/internal/ci/admin-runtime-shard-capacity-20261014.json').read_text())
ORIGINAL = EVIDENCE['originalShard']
NAMES = ('Server Features Admin Runtime Operations', 'Server Features Admin Layer Publishing')
CHILDREN = [next(s for s in CONFIG['shards'] if s['name'] == n) for n in NAMES]
PROJECT = 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'
METHODS = MODULE.selection_pool(MODULE.enumerate_test_classes(), PROJECT)
EXPECTED = {m for m in METHODS if MODULE._eval(MODULE._FilterParser(ORIGINAL['filter']).parse(), m)}
PUBLISHING = set(EVIDENCE['selectedPublishingMethods'])
assert EXPECTED and len(PUBLISHING) == 48 and PUBLISHING < EXPECTED


def check(children):
    selected = [{m for m in METHODS if MODULE._eval(MODULE._FilterParser(s['filter']).parse(), m)} for s in children]
    assert all(selected), 'Empty child.'
    assert set.union(*selected) == EXPECTED, 'Lost original methods or leaked foreign methods.'
    assert sum(map(len, selected)) == len(EXPECTED), 'Duplicate method ownership.'
    assert selected[1] == PUBLISHING, 'Measured publishing methods changed ownership.'
    for child in children:
        assert child['paths'] == ORIGINAL['paths'], 'An original source trigger changed.'
        assert child['csproj'] == ORIGINAL['csproj'] == '', 'Test assembly changed.'
        assert child['max_cpu_count'] == ORIGINAL['max_cpu_count'] == '', 'Parallelism changed.'
        assert (child['test_timeout_minutes'], child['timeout_minutes']) == (27, 37), 'Budget changed.'
    return list(map(len, selected))


counts = check(CHILDREN)
# The canonical partition validator permits one legacy parent per active child.
parents = [p for p in CONFIG['shard_partitions'] if set(NAMES) & set(p['children'])]
assert len(parents) == 1, 'Split children must have exactly one legacy parent.'
parent = parents[0]
assert parent['name'] == 'Server Features Admin Operations'
assert parent['children'] == ['Server Features Admin Operations Endpoints', *NAMES]
assert parent['granularity'] == 'method'
first = sorted(PUBLISHING)[0]
foreign = sorted(set(METHODS) - EXPECTED)[0]
for mutation in ('lost', 'duplicated', 'foreign', 'trigger', 'budget', 'parallelism', 'project'):
    changed = copy.deepcopy(CHILDREN)
    if mutation == 'lost':
        changed[1]['filter'] = '(' + changed[1]['filter'] + ')&FullyQualifiedName!=' + first
    elif mutation == 'duplicated':
        changed[0]['filter'] += '|FullyQualifiedName=' + first
    elif mutation == 'foreign':
        changed[1]['filter'] += '|FullyQualifiedName=' + foreign
    elif mutation == 'trigger':
        changed[0]['paths'] = changed[0]['paths'][1:]
    elif mutation == 'budget':
        changed[1]['test_timeout_minutes'] += 1
    elif mutation == 'parallelism':
        changed[1]['max_cpu_count'] = '2'
    else:
        changed[1]['csproj'] = 'tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj'
    try:
        check(changed)
    except AssertionError:
        pass
    else:
        raise AssertionError(f'{mutation} mutation was accepted')
print(f'admin-runtime-partition=ok original_methods={len(EXPECTED)} child_methods={counts} negatives=7 budgets=unchanged')
