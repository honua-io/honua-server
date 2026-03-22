// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// API response model for a role definition.
/// </summary>
public sealed class RoleResponse
{
    /// <summary>
    /// Role identifier.
    /// </summary>
    public required Guid RoleId { get; init; }

    /// <summary>
    /// Role name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Role description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether this is a built-in role.
    /// </summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// Permission grants for this role.
    /// </summary>
    public IReadOnlyList<PermissionGrantResponse> Permissions { get; init; } = [];

    /// <summary>
    /// When the role was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the role was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Request to create a new role.
/// </summary>
public sealed class CreateRoleRequest
{
    /// <summary>
    /// Role name.
    /// </summary>
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public required string Name { get; init; }

    /// <summary>
    /// Role description.
    /// </summary>
    [StringLength(500)]
    public string? Description { get; init; }
}

/// <summary>
/// Request to update an existing role.
/// </summary>
public sealed class UpdateRoleRequest
{
    /// <summary>
    /// Updated role name.
    /// </summary>
    [StringLength(100, MinimumLength = 1)]
    public string? Name { get; init; }

    /// <summary>
    /// Updated description.
    /// </summary>
    [StringLength(500)]
    public string? Description { get; init; }
}

/// <summary>
/// Request to set permissions for a role.
/// </summary>
public sealed class SetPermissionsRequest
{
    /// <summary>
    /// The complete list of permission grants to assign.
    /// </summary>
    public required IReadOnlyList<PermissionGrantRequest> Permissions { get; init; }
}

/// <summary>
/// A permission grant in a request body.
/// </summary>
public sealed class PermissionGrantRequest
{
    /// <summary>
    /// Service name or "*".
    /// </summary>
    [Required]
    public required string Service { get; init; }

    /// <summary>
    /// Layer identifier or "*".
    /// </summary>
    [Required]
    public required string Layer { get; init; }

    /// <summary>
    /// Operation or "*".
    /// </summary>
    [Required]
    public required string Operation { get; init; }
}
