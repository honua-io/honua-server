#!/usr/bin/env python3
"""Keep the original GP/NAServer methods, triggers and budgets after the capacity split."""
import copy
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('coverage', ROOT / 'scripts/ci/check-server-test-shard-coverage.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
CONFIG = json.loads((ROOT / '.github/ci-shards.json').read_text())
PARENT = '(FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.GPServer|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.NAServer)&FullyQualifiedName!~Honua.Server.Tests.Features.Protocols.GeoServices.GPServer.GPServerEndpointTests'
PATHS = ['src/Honua.Protocols.GeoServices/GPServer/', 'src/Honua.Protocols.GeoServices/NAServer/', 'src/Honua.Routing/Features/Routing/', 'tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/GPServer/', 'tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/NAServer/', 'src/Honua.Core/Features/Validation/']
PROJECT = 'tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj'
NAMES = ('GeoServices GPServer Runtime and NAServer', 'GeoServices GPServer SOAP', 'GeoServices GPServer Task Aliases')
CHILDREN = [next(s for s in CONFIG['shards'] if s['name'] == n) for n in NAMES]
METHODS = MODULE.selection_pool(MODULE.enumerate_test_classes(), PROJECT)
EXPECTED = {m for m in METHODS if MODULE._eval(MODULE._FilterParser(PARENT).parse(), m)}
assert EXPECTED, 'Original runnable source inventory is required.'


def check(children):
    sets = [{m for m in METHODS if MODULE._eval(MODULE._FilterParser(s['filter']).parse(), m)} for s in children]
    assert all(sets), 'Empty child.'
    assert set.union(*sets) == EXPECTED, 'Missing original or foreign methods.'
    assert sum(map(len, sets)) == len(EXPECTED), 'Duplicate ownership.'
    for child in children:
        assert child['csproj'] == PROJECT
        assert child['paths'] == PATHS, 'An original source trigger changed.'
        assert (child['test_timeout_minutes'], child['timeout_minutes']) == (30, 40)
    return list(map(len, sets))


counts = check(CHILDREN)
for mutation in ('lost', 'duplicated', 'foreign', 'trigger', 'budget'):
    changed = copy.deepcopy(CHILDREN)
    if mutation == 'lost':
        changed[1]['filter'] += '&FullyQualifiedName!~GPServerSoapEndpointsTests'
    elif mutation == 'duplicated':
        changed[0]['filter'] += '|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.GPServer.GPServerSoapEndpointsTests.'
    elif mutation == 'foreign':
        changed[1]['filter'] += '|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.GPServer.GPServerEndpointTests.'
    elif mutation == 'trigger':
        changed[0]['paths'] = changed[0]['paths'][1:]
    else:
        changed[1]['test_timeout_minutes'] += 1
    try:
        check(changed)
    except AssertionError:
        pass
    else:
        raise AssertionError(f'{mutation} mutation accepted')
print(f'gp-naserver-partition=ok original_methods={len(EXPECTED)} child_methods={counts} negatives=5 budgets=unchanged')
