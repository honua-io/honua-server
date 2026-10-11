#!/usr/bin/env python3
"""Prove the measured Catalog capacity cut keeps every runnable original method exactly once."""

import copy
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('coverage', ROOT / 'scripts/ci/check-server-test-shard-coverage.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
CONFIG = json.loads((ROOT / '.github/ci-shards.json').read_text(encoding='utf-8'))
PARENT = '(FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.Catalog|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.Tiles|FullyQualifiedName~Honua.Protocols.GeoServices.Tests.Source.ImageServer)&FullyQualifiedName!~ImageServerEndpointsTests'
PROJECT = 'tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj'
NAMES = ('GeoServices Catalog and ImageServer Support', 'GeoServices ImageServer Rendering and Support')
CHILDREN = [next(s for s in CONFIG['shards'] if s['name'] == n) for n in NAMES]
METHODS = MODULE.selection_pool(MODULE.enumerate_test_classes(), PROJECT)
EXPECTED = {m for m in METHODS if MODULE._eval(MODULE._FilterParser(PARENT).parse(), m)}
assert EXPECTED, 'The actual GeoServices source inventory must be present.'


def check(children):
    sets = [{m for m in METHODS if MODULE._eval(MODULE._FilterParser(s['filter']).parse(), m)} for s in children]
    assert all(sets), 'Neither child may run zero methods.'
    assert sets[0] | sets[1] == EXPECTED, 'Lost original methods or leaked foreign methods.'
    assert not sets[0] & sets[1], 'Duplicate method ownership.'
    for s in children:
        assert s['csproj'] == PROJECT
        assert (s['test_timeout_minutes'], s['timeout_minutes']) == (30, 40)
    return [len(s) for s in sets]


counts = check(CHILDREN)
for mutation in ('lost', 'duplicated', 'foreign'):
    changed = copy.deepcopy(CHILDREN)
    if mutation == 'lost':
        changed[1]['filter'] += '&FullyQualifiedName!~ImageServerWmtsReprojectionTests'
    elif mutation == 'duplicated':
        changed[0]['filter'] += '|FullyQualifiedName~ImageServerWmtsReprojectionTests'
    else:
        changed[1]['filter'] += '|FullyQualifiedName~ImageServerEndpointsTests'
    try:
        check(changed)
    except AssertionError:
        pass
    else:
        raise AssertionError(f'{mutation} mutation was accepted')
print(f'geoservices-support-partition=ok original_methods={len(EXPECTED)} child_methods={counts} negatives=3 budgets=unchanged')
