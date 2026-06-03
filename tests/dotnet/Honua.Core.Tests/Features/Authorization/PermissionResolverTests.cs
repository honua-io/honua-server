// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Tests.Features.Authorization;

/// <summary>
/// Unit tests for <see cref="PermissionResolver"/> — the canonical per-operation
/// permission resolver (#1375). Locks in the grant-resolution decision matrix:
/// wildcard precedence, per-layer / per-operation granularity, read/query
/// synonym handling, the no-matching-grant fallback signal, and the anonymous
/// requires-authentication signal.
/// </summary>
public sealed class PermissionResolverTests
{
    private static EffectivePermissions Effective(params PermissionGrant[] grants)
        => new()
        {
            UserId = "user-1",
            Roles = ["role-a"],
            Permissions = grants,
        };

    private static PermissionGrant Grant(string service, string layer, string operation)
        => new() { Service = service, Layer = layer, Operation = operation };

    [Fact]
    public void Authorize_WildcardGrant_AllowsEveryOperation()
    {
        var resolver = new PermissionResolver(new FakeRoleStore());
        var effective = Effective(Grant("*", "*", "*"));

        resolver.Authorize(effective, "svc", "layer-1", AuthorizationOperation.Delete)
            .IsAllowed.Should().BeTrue();
        resolver.Authorize(effective, "any", "any", AuthorizationOperation.Query)
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Authorize_ServiceLevelGrant_ImpliesAllLayers()
    {
        var resolver = new PermissionResolver(new FakeRoleStore());
        var effective = Effective(Grant("svc", "*", "query"));

        resolver.Authorize(effective, "svc", "layer-1", AuthorizationOperation.Query)
            .IsAllowed.Should().BeTrue();
        resolver.Authorize(effective, "svc", "layer-99", AuthorizationOperation.Query)
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Authorize_PerLayerGrant_DeniesOtherLayers()
    {
        var resolver = new PermissionResolver(new FakeRoleStore());
        var effective = Effective(Grant("svc", "layer-A", "query"));

        resolver.Authorize(effective, "svc", "layer-A", AuthorizationOperation.Query)
            .IsAllowed.Should().BeTrue();

        var denied = resolver.Authorize(effective, "svc", "layer-B", AuthorizationOperation.Query);
        denied.IsAllowed.Should().BeFalse();
        denied.HasNoMatchingGrant.Should().BeTrue();
    }

    [Fact]
    public void Authorize_PerOperationGrant_DeniesOtherOperations()
    {
        var resolver = new PermissionResolver(new FakeRoleStore());
        var effective = Effective(Grant("svc", "layer-A", "query"));

        resolver.Authorize(effective, "svc", "layer-A", AuthorizationOperation.Query)
            .IsAllowed.Should().BeTrue();
        resolver.Authorize(effective, "svc", "layer-A", AuthorizationOperation.Delete)
            .HasNoMatchingGrant.Should().BeTrue();
    }

    [Fact]
    public void Authorize_LegacyReadGrant_SatisfiesQuery()
    {
        var resolver = new PermissionResolver(new FakeRoleStore());
        var effective = Effective(Grant("svc", "*", "read"));

        // The historic "read" grant must cover the canonical Query operation so
        // existing seeded grants keep working after the taxonomy expansion.
        resolver.Authorize(effective, "svc", "layer-1", AuthorizationOperation.Query)
            .IsAllowed.Should().BeTrue();
        resolver.Authorize(effective, "svc", "layer-1", AuthorizationOperation.Read)
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Authorize_NullLayer_TreatedAsWildcardLayer()
    {
        var resolver = new PermissionResolver(new FakeRoleStore());
        var effective = Effective(Grant("svc", "*", "metadata"));

        resolver.Authorize(effective, "svc", layer: null, AuthorizationOperation.Metadata)
            .IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Authorize_NoGrants_ReturnsNoMatch()
    {
        var resolver = new PermissionResolver(new FakeRoleStore());

        resolver.Authorize(Effective(), "svc", "layer-1", AuthorizationOperation.Query)
            .HasNoMatchingGrant.Should().BeTrue();
    }

    [Fact]
    public async Task AuthorizeAsync_AnonymousWithNoGrant_RequiresAuthentication()
    {
        var resolver = new PermissionResolver(new FakeRoleStore());

        var decision = await resolver.AuthorizeAsync(
            userId: string.Empty,
            roles: [],
            service: "svc",
            layer: "layer-1",
            operation: AuthorizationOperation.Query,
            isAuthenticated: false);

        decision.Result.Should().Be(PermissionResult.RequiresAuthentication);
    }

    [Fact]
    public async Task AuthorizeAsync_AuthenticatedWithNoGrant_ReturnsNoMatchForFallback()
    {
        var resolver = new PermissionResolver(new FakeRoleStore());

        var decision = await resolver.AuthorizeAsync(
            userId: "user-1",
            roles: ["unknown-role"],
            service: "svc",
            layer: "layer-1",
            operation: AuthorizationOperation.Query,
            isAuthenticated: true);

        decision.HasNoMatchingGrant.Should().BeTrue();
    }

    [Fact]
    public async Task AuthorizeAsync_RoleWithGrant_Allows()
    {
        var store = new FakeRoleStore
        {
            GrantsByRole =
            {
                ["editor"] = [Grant("svc", "*", "query")],
            },
        };
        var resolver = new PermissionResolver(store);

        var decision = await resolver.AuthorizeAsync(
            userId: "user-1",
            roles: ["editor"],
            service: "svc",
            layer: "layer-1",
            operation: AuthorizationOperation.Query,
            isAuthenticated: true);

        decision.IsAllowed.Should().BeTrue();
    }

    private sealed class FakeRoleStore : IRoleStore
    {
        public Dictionary<string, IReadOnlyList<PermissionGrant>> GrantsByRole { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            string userId,
            IReadOnlyList<string> roles,
            CancellationToken cancellationToken = default)
        {
            var grants = roles
                .SelectMany(role => GrantsByRole.TryGetValue(role, out var g) ? g : [])
                .Distinct()
                .ToList();

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
