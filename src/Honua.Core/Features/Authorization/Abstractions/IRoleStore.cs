// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Store for role definitions and permission grants.
/// </summary>
public interface IRoleStore
{
    /// <summary>
    /// Lists all role definitions.
    /// </summary>
    Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific role by ID.
    /// </summary>
    Task<RoleDefinition?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new role definition.
    /// </summary>
    Task<RoleDefinition> CreateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing role definition.
    /// </summary>
    Task<RoleDefinition?> UpdateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a role definition. Built-in roles cannot be deleted.
    /// </summary>
    Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the permission grants for a specific role.
    /// </summary>
    Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces all permission grants for a role.
    /// </summary>
    Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(Guid roleId, IReadOnlyList<PermissionGrant> permissions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves effective permissions for a user across all their role memberships.
    /// </summary>
    Task<EffectivePermissions> GetEffectivePermissionsAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken = default);
}
