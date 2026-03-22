// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>
/// Represents a named role with associated permission grants.
/// </summary>
public sealed class RoleDefinition
{
    /// <summary>
    /// Unique identifier for this role.
    /// </summary>
    public Guid RoleId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable role name (e.g., "editor", "viewer", "field-worker").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description of the role's purpose.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether this is a built-in role that cannot be deleted.
    /// </summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// Permission grants associated with this role.
    /// </summary>
    public IReadOnlyList<PermissionGrant> Permissions { get; init; } = [];

    /// <summary>
    /// When the role was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the role was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A permission tuple granting access to a specific service/layer/operation combination.
/// </summary>
public sealed class PermissionGrant
{
    /// <summary>
    /// Service name this permission applies to, or "*" for all services.
    /// </summary>
    public required string Service { get; init; }

    /// <summary>
    /// Layer identifier this permission applies to, or "*" for all layers.
    /// </summary>
    public required string Layer { get; init; }

    /// <summary>
    /// Operation this permission grants (e.g., "read", "write", "delete", "*").
    /// </summary>
    public required string Operation { get; init; }
}

/// <summary>
/// Resolved effective permissions for a user across all role memberships.
/// </summary>
public sealed class EffectivePermissions
{
    /// <summary>
    /// The user ID these permissions are resolved for.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// All roles the user is a member of.
    /// </summary>
    public required IReadOnlyList<string> Roles { get; init; }

    /// <summary>
    /// The combined set of permission grants from all role memberships.
    /// </summary>
    public required IReadOnlyList<PermissionGrant> Permissions { get; init; }

    /// <summary>
    /// When these permissions were resolved.
    /// </summary>
    public DateTimeOffset ResolvedAt { get; init; } = DateTimeOffset.UtcNow;
}
