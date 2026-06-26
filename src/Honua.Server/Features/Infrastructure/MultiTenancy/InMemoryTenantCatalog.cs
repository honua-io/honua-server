// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>
/// Process-local tenant catalog backing the tenant lifecycle admin surface (issue #2156).
/// </summary>
/// <remarks>
/// Like the in-memory rate-limit policy store and usage meter, this is the thin shipped seam:
/// tenants are held per-node so operators can model the create/suspend/resume/delete lifecycle
/// without standing up durable provisioning infrastructure. A durable, cluster-shared
/// (schema/migration backed) implementation can replace this without changing callers.
/// </remarks>
internal sealed class InMemoryTenantCatalog : ITenantCatalog
{
    private readonly ConcurrentDictionary<string, TenantRecord> _tenants =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<TenantRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        _tenants.TryGetValue(tenantId, out var record);
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TenantRecord> result = _tenants.Values
            .OrderBy(t => t.TenantId, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
        return Task.FromResult(result);
    }

    public Task<bool> TryAddAsync(TenantRecord tenant, CancellationToken cancellationToken = default)
        => Task.FromResult(_tenants.TryAdd(tenant.TenantId, tenant));

    public Task<TenantRecord?> UpdateAsync(TenantRecord tenant, CancellationToken cancellationToken = default)
    {
        if (!_tenants.ContainsKey(tenant.TenantId))
        {
            return Task.FromResult<TenantRecord?>(null);
        }

        _tenants[tenant.TenantId] = tenant;
        return Task.FromResult<TenantRecord?>(tenant);
    }
}
