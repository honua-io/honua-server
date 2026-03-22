// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// API response model for a managed user.
/// </summary>
public sealed class UserResponse
{
    /// <summary>
    /// User identifier.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Email address.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// How the user was provisioned.
    /// </summary>
    public required string ProvisioningSource { get; init; }

    /// <summary>
    /// OIDC provider ID, if applicable.
    /// </summary>
    public Guid? ProviderId { get; init; }

    /// <summary>
    /// Whether the user is active.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Assigned roles.
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>
    /// When the user was provisioned.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the user was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Paginated user list response.
/// </summary>
public sealed class UserListResponse
{
    /// <summary>
    /// Users in this page.
    /// </summary>
    public required IReadOnlyList<UserResponse> Users { get; init; }

    /// <summary>
    /// Total users matching the filter.
    /// </summary>
    public required int TotalCount { get; init; }
}

/// <summary>
/// Request to update a user's role assignments.
/// </summary>
public sealed class UpdateUserRolesRequest
{
    /// <summary>
    /// The complete list of roles to assign to the user.
    /// </summary>
    public required IReadOnlyList<string> Roles { get; init; }
}

/// <summary>
/// API response for effective permissions resolved across all role memberships.
/// </summary>
public sealed class EffectivePermissionsResponse
{
    /// <summary>
    /// User ID.
    /// </summary>
    public required string UserId { get; init; }

    /// <summary>
    /// Roles the user is a member of.
    /// </summary>
    public required IReadOnlyList<string> Roles { get; init; }

    /// <summary>
    /// Resolved permission grants.
    /// </summary>
    public required IReadOnlyList<PermissionGrantResponse> Permissions { get; init; }

    /// <summary>
    /// When these permissions were resolved.
    /// </summary>
    public DateTimeOffset ResolvedAt { get; init; }
}

/// <summary>
/// API response model for a permission grant.
/// </summary>
public sealed class PermissionGrantResponse
{
    /// <summary>
    /// Service name or "*".
    /// </summary>
    public required string Service { get; init; }

    /// <summary>
    /// Layer identifier or "*".
    /// </summary>
    public required string Layer { get; init; }

    /// <summary>
    /// Operation or "*".
    /// </summary>
    public required string Operation { get; init; }
}
