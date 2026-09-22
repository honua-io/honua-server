// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Tenant boundary for <see cref="StudioAuthorizationService"/> (honua-server#4905): the
/// platform <c>admin</c> role is tenant-scoped, so it no longer bypasses the ownership check on
/// another tenant's Studio content, while single-tenant and multi-tenant-admin behaviour is
/// unchanged.
/// </summary>
public sealed class StudioTenantOwnershipAuthorizationTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string DefaultTenant = "public";

    [UnitTest]
    public async Task AuthorizeAsync_TenantScopedAdmin_AnotherTenantsResource_IsDeniedBeforeTheAdminBypass()
    {
        var service = BuildService(requestTenantId: TenantB);

        var decision = await service.AuthorizeAsync(
            AdminPrincipal(),
            "admin-b",
            StudioAuthorizationOperation.ReadContentItem,
            resourceOwnerId: "owner-a",
            resourceTenantId: TenantA);

        Assert.False(decision.IsAllowed);
        Assert.Equal(StudioAuthorizationService.CrossTenantDeniedCode, decision.Code);
    }

    [UnitTest]
    public async Task AuthorizeAsync_TenantScopedAdmin_OwnTenantsResource_IsAllowed()
    {
        var service = BuildService(requestTenantId: TenantA);

        var decision = await service.AuthorizeAsync(
            AdminPrincipal(),
            "admin-a",
            StudioAuthorizationOperation.ReadContentItem,
            resourceOwnerId: "owner-a",
            resourceTenantId: TenantA);

        Assert.True(decision.IsAllowed);
    }

    [UnitTest]
    public async Task AuthorizeAsync_TenantUnassignedResource_IsReachableOnlyFromTheDefaultTenant()
    {
        // A record written before tenant stamping (or by a background worker with no request
        // tenant) belongs to the deployment default, which is exactly what an upgraded
        // single-tenant deployment's requests resolve to.
        var defaultTenantRequest = await BuildService(requestTenantId: DefaultTenant).AuthorizeAsync(
            AdminPrincipal(),
            "admin-1",
            StudioAuthorizationOperation.ReadContentItem,
            resourceOwnerId: "legacy-owner",
            resourceTenantId: null);
        Assert.True(defaultTenantRequest.IsAllowed);

        var otherTenantRequest = await BuildService(requestTenantId: TenantB).AuthorizeAsync(
            AdminPrincipal(),
            "admin-b",
            StudioAuthorizationOperation.ReadContentItem,
            resourceOwnerId: "legacy-owner",
            resourceTenantId: null);
        Assert.False(otherTenantRequest.IsAllowed);
        Assert.Equal(StudioAuthorizationService.CrossTenantDeniedCode, otherTenantRequest.Code);
    }

    [UnitTest]
    public async Task AuthorizeAsync_MultiTenantAdmin_AnotherTenantsResource_IsAllowed()
    {
        var service = BuildService(requestTenantId: TenantB);

        var decision = await service.AuthorizeAsync(
            AdminPrincipal("platform_admin"),
            "platform-1",
            StudioAuthorizationOperation.ReadContentItem,
            resourceOwnerId: "owner-a",
            resourceTenantId: TenantA);

        Assert.True(decision.IsAllowed);
    }

    [UnitTest]
    public async Task AuthorizeAsync_TenantResolutionDisabled_IsUnchanged()
    {
        var service = BuildService(requestTenantId: null, tenantIsolation: new TenantIsolationOptions { Enabled = false });

        var decision = await service.AuthorizeAsync(
            AdminPrincipal(),
            "admin-1",
            StudioAuthorizationOperation.ReadContentItem,
            resourceOwnerId: "owner-a",
            resourceTenantId: TenantA);

        Assert.True(decision.IsAllowed);
    }

    [UnitTest]
    public async Task AuthorizeAsync_HostWithoutTenantResolution_IsUnchanged()
    {
        // No ITenantContext registered at all: there is no tenant rail to enforce.
        var service = BuildService(requestTenantId: null, registerTenantContext: false);

        var decision = await service.AuthorizeAsync(
            AdminPrincipal(),
            "admin-1",
            StudioAuthorizationOperation.ReadContentItem,
            resourceOwnerId: "owner-a",
            resourceTenantId: TenantA);

        Assert.True(decision.IsAllowed);
    }

    [UnitTest]
    public void CreateTenantScopeFilter_ScopesEnumerationToTheRequestTenant()
    {
        var defaultTenant = BuildService(requestTenantId: DefaultTenant).CreateTenantScopeFilter(AdminPrincipal());
        Assert.Equal(new TenantScopeFilter(DefaultTenant, true), defaultTenant);

        var otherTenant = BuildService(requestTenantId: TenantB).CreateTenantScopeFilter(AdminPrincipal());
        Assert.Equal(new TenantScopeFilter(TenantB, false), otherTenant);
    }

    [UnitTest]
    public void CreateTenantScopeFilter_MultiTenantAdminOrDisabledTenancy_IsUnscoped()
    {
        Assert.Null(BuildService(requestTenantId: TenantB).CreateTenantScopeFilter(AdminPrincipal("platform_admin")));
        Assert.Null(BuildService(
                requestTenantId: TenantB,
                tenantIsolation: new TenantIsolationOptions { Enabled = false })
            .CreateTenantScopeFilter(AdminPrincipal()));
        Assert.Null(BuildService(requestTenantId: null, registerTenantContext: false)
            .CreateTenantScopeFilter(AdminPrincipal()));
    }

    private static StudioAuthorizationService BuildService(
        string? requestTenantId,
        TenantIsolationOptions? tenantIsolation = null,
        bool registerTenantContext = true)
        => new(
            new DenyingOperatorAuthorizationEvaluator(),
            new OperatorScopeAuthorizer(),
            new StaticOptionsMonitor<StudioEndUserAuthorizationOptions>(new StudioEndUserAuthorizationOptions { Enabled = true }),
            new StaticOptionsMonitor<AdminRoleOptions>(new AdminRoleOptions { AdminRoles = [] }),
            registerTenantContext ? new StaticTenantContext(requestTenantId) : null,
            new StaticOptionsMonitor<TenantIsolationOptions>(tenantIsolation ?? new TenantIsolationOptions()));

    private static ClaimsPrincipal AdminPrincipal(string? extraRole = null)
    {
        List<Claim> claims =
        [
            new Claim(ClaimTypes.NameIdentifier, "admin-1"),
            new Claim(ClaimTypes.Role, "admin"),
        ];
        if (extraRole is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, extraRole));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    private sealed class StaticTenantContext(string? tenantId) : ITenantContext
    {
        public string? TenantId { get; } = tenantId;

        public TenantContextSource Source => TenantContextSource.Claim;

        public bool RequireTenantId(out string tenantId, out string? reason)
        {
            tenantId = TenantId ?? string.Empty;
            reason = TenantId is null ? "no tenant claim present" : null;
            return TenantId is not null;
        }
    }

    private sealed class DenyingOperatorAuthorizationEvaluator : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AccessDecision.Forbidden("no grant"));
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
