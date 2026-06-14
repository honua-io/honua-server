// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Startup;

/// <summary>
/// Regression guard for #1575: the durable provider role store (e.g. PostgresRoleStore,
/// registered earlier via AddPostgreSqlServices) must not be shadowed by the in-memory
/// control-plane default. The default is registered with TryAdd so durable wins.
/// </summary>
public sealed class ControlPlaneIamDefaultsTests
{
    [Fact]
    public void AddInMemoryControlPlaneIamDefaults_PreservesDurableRoleStoreRegisteredEarlier()
    {
        var services = new ServiceCollection();

        // A durable provider (as AddPostgreSqlServices does) registers its scoped store first.
        services.AddScoped<IRoleStore, FakeDurableRoleStore>();

        // The in-memory defaults are registered afterwards, mirroring Program.cs ordering.
        services.AddInMemoryControlPlaneIamDefaults();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<IRoleStore>();

        // Durable store wins; with the old unconditional AddSingleton this would resolve
        // to InMemoryRoleStore and grants would not persist across restarts.
        Assert.IsType<FakeDurableRoleStore>(resolved);
    }

    [Fact]
    public void AddInMemoryControlPlaneIamDefaults_FallsBackToInMemoryWhenNoDurableStore()
    {
        var services = new ServiceCollection();

        services.AddInMemoryControlPlaneIamDefaults();

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IRoleStore>();

        Assert.IsType<InMemoryRoleStore>(resolved);
    }

    private sealed class FakeDurableRoleStore : IRoleStore
    {
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

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken = default)
            => Task.FromResult(new EffectivePermissions { UserId = userId, Roles = roles, Permissions = [] });
    }
}
