// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class UserResponse
{
    public required string UserId { get; init; }

    public required string DisplayName { get; init; }

    public string? Email { get; init; }

    public required string ProvisioningSource { get; init; }

    public Guid? ProviderId { get; init; }

    public bool IsActive { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class UserListResponse
{
    public required IReadOnlyList<UserResponse> Users { get; init; }

    public required int TotalCount { get; init; }
}

public sealed class UpdateUserRolesRequest
{
    public required IReadOnlyList<string> Roles { get; init; }
}

public sealed class EffectivePermissionsResponse
{
    public required string UserId { get; init; }

    public required IReadOnlyList<string> Roles { get; init; }

    public required IReadOnlyList<PermissionGrantResponse> Permissions { get; init; }

    public DateTimeOffset ResolvedAt { get; init; }
}

public sealed class PermissionGrantResponse
{
    public required string Service { get; init; }

    public required string Layer { get; init; }

    public required string Operation { get; init; }
}
