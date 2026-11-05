// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

public sealed class RoleResponse
{
    public required Guid RoleId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool IsBuiltIn { get; init; }

    public IReadOnlyList<PermissionGrantResponse> Permissions { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class CreateRoleRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public required string Name { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }
}

public sealed class UpdateRoleRequest
{
    [StringLength(100, MinimumLength = 1)]
    public string? Name { get; init; }

    [StringLength(500)]
    public string? Description { get; init; }
}

public sealed class SetPermissionsRequest
{
    public required IReadOnlyList<PermissionGrantRequest> Permissions { get; init; }
}

public sealed class PermissionGrantRequest
{
    [Required]
    public required string Service { get; init; }

    [Required]
    public required string Layer { get; init; }

    [Required]
    public required string Operation { get; init; }
}
