// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Identity.Domain;
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

    [Fact]
    public void AddInMemoryControlPlaneIamDefaults_PreservesDurableUserStoresRegisteredEarlier()
    {
        // #3141: the durable PostgresUserStore / PostgresScimGroupStore registered by
        // AddPostgreSqlServices (earlier in Program.cs) must win over the in-memory
        // defaults for ALL THREE user contracts, or SCIM provisioning and the admin
        // surface would silently split across different record sets.
        var services = new ServiceCollection();

        services.AddSingleton<IUserStore, FakeDurableUserStore>();
        services.AddSingleton<IScimUserStore, FakeDurableUserStore>();
        services.AddSingleton<IScimGroupStore, FakeDurableScimGroupStore>();

        services.AddInMemoryControlPlaneIamDefaults();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<FakeDurableUserStore>(provider.GetRequiredService<IUserStore>());
        Assert.IsType<FakeDurableUserStore>(provider.GetRequiredService<IScimUserStore>());
        Assert.IsType<FakeDurableScimGroupStore>(provider.GetRequiredService<IScimGroupStore>());

        // The membership source still resolves over whatever IUserStore won.
        Assert.IsType<ManagedUserPrincipalMembershipSource>(
            provider.GetRequiredService<IPrincipalMembershipSource>());
    }

    [Fact]
    public void AddInMemoryControlPlaneIamDefaults_UserContracts_FallBackToSharedInMemoryInstance()
    {
        var services = new ServiceCollection();

        services.AddInMemoryControlPlaneIamDefaults();

        using var provider = services.BuildServiceProvider();
        var userStore = provider.GetRequiredService<IUserStore>();
        var scimStore = provider.GetRequiredService<IScimUserStore>();

        // Both contracts project onto the SAME in-memory instance so SCIM-provisioned
        // users are visible to the admin endpoints.
        Assert.IsType<InMemoryUserStore>(userStore);
        Assert.Same(userStore, scimStore);
        Assert.IsType<InMemoryScimGroupStore>(provider.GetRequiredService<IScimGroupStore>());
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

    private sealed class FakeDurableUserStore : IUserStore, IScimUserStore
    {
        public Task<UserListResult> ListUsersAsync(UserListFilter filter, CancellationToken cancellationToken = default)
            => Task.FromResult(new UserListResult { Users = [], TotalCount = 0 });

        public Task<ManagedUser?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
            => Task.FromResult<ManagedUser?>(null);

        public Task<ManagedUser?> UpdateUserRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken = default)
            => Task.FromResult<ManagedUser?>(null);

        public Task<bool> DeprovisionUserAsync(string userId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<ManagedUser?> CreateUserAsync(ScimUserProvisioning provisioning, CancellationToken cancellationToken = default)
            => Task.FromResult<ManagedUser?>(null);

        public Task<ManagedUser?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default)
            => Task.FromResult<ManagedUser?>(null);

        public Task<ScimUserPage> ListUsersAsync(ScimUserQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new ScimUserPage { Users = [], TotalCount = 0 });

        public Task<ManagedUser?> ReplaceUserAsync(string userId, ScimUserProvisioning provisioning, CancellationToken cancellationToken = default)
            => Task.FromResult<ManagedUser?>(null);

        public Task<ManagedUser?> SetActiveAsync(string userId, bool active, CancellationToken cancellationToken = default)
            => Task.FromResult<ManagedUser?>(null);

        public Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class FakeDurableScimGroupStore : IScimGroupStore
    {
        public Task<ScimGroup?> CreateGroupAsync(ScimGroupProvisioning provisioning, CancellationToken cancellationToken = default)
            => Task.FromResult<ScimGroup?>(null);

        public Task<ScimGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken = default)
            => Task.FromResult<ScimGroup?>(null);

        public Task<ScimGroupPage> ListGroupsAsync(ScimGroupQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new ScimGroupPage { Groups = [], TotalCount = 0 });

        public Task<ScimGroup?> ReplaceGroupAsync(string groupId, ScimGroupProvisioning provisioning, CancellationToken cancellationToken = default)
            => Task.FromResult<ScimGroup?>(null);

        public Task<ScimGroup?> UpdateMembersAsync(string groupId, ScimGroupMemberChange change, CancellationToken cancellationToken = default)
            => Task.FromResult<ScimGroup?>(null);

        public Task<bool> DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
