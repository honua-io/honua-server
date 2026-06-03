// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Security;

/// <summary>
/// Cross-protocol smoke proving the canonical permission resolver (#1375) is
/// LIVE on an already-enforced path: the GeoServices FeatureServer layer-query
/// read seam now consults per-operation RBAC grants and the legacy coarse
/// <see cref="AccessPolicy"/> only as a fallback.
/// </summary>
public sealed class PermissionResolverQuerySmokeTests
{
    private const string GrantedRole = "alpha-query-grant";
    private const string QueryUrl =
        "/rest/services/" + ServiceRbacTestFixture.AlphaService +
        "/FeatureServer/" + "0" + "/query?where=1=1&f=json";

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_PerOperationGrant_OverridesCoarseReadPolicyDeny()
    {
        // Alpha service requires a coarse read role the user does NOT hold, so
        // the legacy AccessPolicy seam would 403. The user instead holds a role
        // whose RBAC store grants the "query" operation on service "alpha", which
        // the resolver must honor → 200.
        using var factory = ServiceRbacTestFixture.CreateFactory(
            layerCatalogFactory: static () => new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["coarse-only-role"])),
            configureServices: static services =>
            {
                services.RemoveAll<ICrsDetectionService>();
                services.AddSingleton<ICrsDetectionService, NoopCrsDetectionService>();
                services.RemoveAll<IRoleStore>();
                services.AddSingleton<IRoleStore>(new StubRoleStore(GrantedRole,
                    new PermissionGrant { Service = ServiceRbacTestFixture.AlphaService, Layer = "*", Operation = "query" }));
            });

        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.GetAsync(QueryUrl);

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_NoGrant_FallsBackToCoarsePolicyDeny()
    {
        // Same coarse deny, but the user's role carries NO matching grant, so the
        // resolver returns no-match and the coarse AccessPolicy fallback applies
        // → 403 (proving no behavior regression for ungranted principals).
        using var factory = ServiceRbacTestFixture.CreateFactory(
            layerCatalogFactory: static () => new RbacTestLayerCatalog(
                alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["coarse-only-role"])),
            configureServices: static services =>
            {
                services.RemoveAll<ICrsDetectionService>();
                services.AddSingleton<ICrsDetectionService, NoopCrsDetectionService>();
                services.RemoveAll<IRoleStore>();
                services.AddSingleton<IRoleStore>(new StubRoleStore("some-other-role",
                    new PermissionGrant { Service = "beta", Layer = "*", Operation = "query" }));
            });

        using var client = ServiceRbacTestFixture.CreateClient(factory, GrantedRole);

        var response = await client.GetAsync(QueryUrl);

        await ServiceRbacTestFixture.AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Minimal <see cref="IRoleStore"/> that resolves the supplied grant for a
    /// single named role; everything else is empty.
    /// </summary>
    private sealed class StubRoleStore(string roleName, PermissionGrant grant) : IRoleStore
    {
        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            string userId,
            IReadOnlyList<string> roles,
            CancellationToken cancellationToken = default)
        {
            var grants = roles.Any(r => string.Equals(r, roleName, StringComparison.OrdinalIgnoreCase))
                ? new[] { grant }
                : [];

            return Task.FromResult(new EffectivePermissions
            {
                UserId = userId,
                Roles = roles,
                Permissions = grants,
            });
        }

        public Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RoleDefinition>>([]);

        public Task<RoleDefinition?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult<RoleDefinition?>(null);

        public Task<RoleDefinition> CreateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
            => Task.FromResult(role);

        public Task<RoleDefinition?> UpdateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
            => Task.FromResult<RoleDefinition?>(role);

        public Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PermissionGrant>>([]);

        public Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(Guid roleId, IReadOnlyList<PermissionGrant> permissions, CancellationToken cancellationToken = default)
            => Task.FromResult(permissions);
    }
}
