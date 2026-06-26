// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.MultiTenancy;

/// <summary>
/// Unit tests for suspended/deleted tenant enforcement (issue #2156). Suspension must block access
/// for both reads and writes, while unmanaged and active tenants pass through unchanged.
/// </summary>
public sealed class TenantStatusEnforcementMiddlewareTests
{
    [UnitTest]
    public async Task InvokeAsync_SuspendedTenant_Returns403AndDoesNotCallNext()
    {
        var nextCalled = false;
        var middleware = new TenantStatusEnforcementMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext("acme", TenantStatus.Suspended);
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        nextCalled.Should().BeFalse("a suspended tenant must be blocked before reaching the handler");
    }

    [UnitTest]
    public async Task InvokeAsync_DeletedTenant_Returns403()
    {
        var middleware = new TenantStatusEnforcementMiddleware(_ => Task.CompletedTask);

        var context = CreateContext("acme", TenantStatus.Deleted);
        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [UnitTest]
    public async Task InvokeAsync_ActiveTenant_PassesThrough()
    {
        var nextCalled = false;
        var middleware = new TenantStatusEnforcementMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext("acme", TenantStatus.Active);
        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [UnitTest]
    public async Task InvokeAsync_UnmanagedTenant_PassesThrough()
    {
        var nextCalled = false;
        var middleware = new TenantStatusEnforcementMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        // "public" tenant resolved but not present in the catalog -> unmanaged -> allowed.
        var context = CreateContext("public", catalogTenant: null);
        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
    }

    private static DefaultHttpContext CreateContext(string tenantId, TenantStatus catalogStatus)
        => CreateContext(tenantId, new TenantRecord
        {
            TenantId = tenantId,
            DisplayName = tenantId,
            Status = catalogStatus,
        });

    private static DefaultHttpContext CreateContext(string tenantId, TenantRecord? catalogTenant)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Response.StatusCode = StatusCodes.Status200OK;

        var catalog = new StubCatalog();
        if (catalogTenant is not null)
        {
            catalog.Add(catalogTenant);
        }

        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StubTenantContext(tenantId));
        services.AddSingleton<ITenantCatalog>(catalog);
        context.RequestServices = services.BuildServiceProvider();

        return context;
    }

    private sealed class StubCatalog : ITenantCatalog
    {
        private readonly ConcurrentDictionary<string, TenantRecord> _tenants =
            new(StringComparer.OrdinalIgnoreCase);

        public void Add(TenantRecord record) => _tenants[record.TenantId] = record;

        public Task<TenantRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            _tenants.TryGetValue(tenantId, out var record);
            return Task.FromResult(record);
        }

        public Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TenantRecord>>(_tenants.Values.ToList());

        public Task<bool> TryAddAsync(TenantRecord tenant, CancellationToken cancellationToken = default)
            => Task.FromResult(_tenants.TryAdd(tenant.TenantId, tenant));

        public Task<TenantRecord?> UpdateAsync(TenantRecord tenant, CancellationToken cancellationToken = default)
        {
            _tenants[tenant.TenantId] = tenant;
            return Task.FromResult<TenantRecord?>(tenant);
        }
    }

    private sealed class StubTenantContext(string? tenantId) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;

        public TenantContextSource Source { get; } =
            tenantId is null ? TenantContextSource.Anonymous : TenantContextSource.Claim;

        public bool RequireTenantId(out string resolvedTenantId, out string? reason)
        {
            if (string.IsNullOrEmpty(TenantId))
            {
                resolvedTenantId = string.Empty;
                reason = "no tenant context resolved";
                return false;
            }

            resolvedTenantId = TenantId;
            reason = null;
            return true;
        }
    }
}
