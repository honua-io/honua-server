#!/usr/bin/env python3
"""Keep every original Admin Authorization method exactly once after the measured cut."""
import copy
import importlib.util
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('coverage', ROOT / 'scripts/ci/check-server-test-shard-coverage.py')
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
CONFIG = json.loads((ROOT / '.github/ci-shards.json').read_text())
PARENT = '((FullyQualifiedName~Honua.Server.Tests.Features.Admin.)&FullyQualifiedName!~RoleEndpointsTests&(FullyQualifiedName~AdminAuth|FullyQualifiedName~OAuth|FullyQualifiedName~Oidc|FullyQualifiedName~ApiKey|FullyQualifiedName~ClientCertificate|FullyQualifiedName~OperatorBearerToken|FullyQualifiedName~UserManagement|FullyQualifiedName~RowLevelSecurity|FullyQualifiedName~RateLimit|FullyQualifiedName~Tenant))&FullyQualifiedName!~Honua.Server.Tests.Features.Admin.AdminAuthEndpointsTests&FullyQualifiedName!~Honua.Server.Tests.Features.Admin.OAuthClientEndpointsTests&FullyQualifiedName!~Honua.Server.Tests.Features.Admin.OidcJwtBearerValidationTests&FullyQualifiedName!~Honua.Server.Tests.Features.Admin.OidcProviderEndpointsTests&FullyQualifiedName!~Honua.Server.Tests.Features.Admin.AdminApiKeyEndpointsTests&FullyQualifiedName!~Honua.Server.Tests.Features.Admin.ClientCertificateAdminEndpointsTests&FullyQualifiedName!~Honua.Server.Tests.Features.Admin.OperatorBearerTokenServiceTests'
PATHS = ['src/Honua.Server/Features/Admin/', 'src/Honua.Server/Features/Admin/AdminEndpoints.cs', 'src/Honua.Server/Features/Admin/IdentityAdmin', 'src/Honua.Server/Features/Admin/RateLimit', 'src/Honua.Server/Features/Admin/RlsPolicy', 'src/Honua.Server/Features/Admin/TenantAdmin', 'src/Honua.Server/Features/Admin/UserManagement', 'src/Honua.Server/Features/Admin/Models/RateLimit', 'src/Honua.Server/Features/Admin/Models/RlsPolicy', 'src/Honua.Server/Features/Admin/Models/Tenant', 'src/Honua.Server/Features/Admin/Models/UserManagement', 'tests/dotnet/Honua.Server.Tests/Features/Admin/AdminAuthorizationTests.cs', 'tests/dotnet/Honua.Server.Tests/Features/Admin/RateLimitEndpointsTests.cs', 'tests/dotnet/Honua.Server.Tests/Features/Admin/RowLevelSecurityEndpointsTests.cs', 'tests/dotnet/Honua.Server.Tests/Features/Admin/TenantAdminEndpointsTests.cs', 'tests/dotnet/Honua.Server.Tests/Features/Admin/UserManagementEndpointsTests.cs', 'src/Honua.Core/Features/Validation/']
PROJECT = 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'
NAMES = ('Server Features Admin Authorization', 'Server Features Admin Authorization Matrix')
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
        assert child['csproj'] == ''
        assert child['paths'] == PATHS, 'An original source trigger changed.'
        assert (child['test_timeout_minutes'], child['timeout_minutes']) == (22, 32)
    return list(map(len, sets))


counts = check(CHILDREN)
for mutation in ('lost', 'duplicated', 'foreign', 'trigger', 'budget'):
    changed = copy.deepcopy(CHILDREN)
    if mutation == 'lost':
        changed[1]['filter'] += '&FullyQualifiedName!~GetConfig_WithoutAuth_Returns401'
    elif mutation == 'duplicated':
        changed[0]['filter'] += '|FullyQualifiedName~Honua.Server.Tests.Features.Admin.AdminAuthorizationTests.'
    elif mutation == 'foreign':
        changed[1]['filter'] += '|FullyQualifiedName~Honua.Server.Tests.Features.Admin.AdminAuthEndpointsTests.'
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
print(f'admin-authorization-partition=ok original_methods={len(EXPECTED)} child_methods={counts} negatives=5 budgets=unchanged')
