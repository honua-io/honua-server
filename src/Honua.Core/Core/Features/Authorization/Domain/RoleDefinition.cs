// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

public sealed class RoleDefinition
{
    public Guid RoleId { get; init; } = Guid.NewGuid();

    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool IsBuiltIn { get; init; }

    public IReadOnlyList<PermissionGrant> Permissions { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PermissionGrant
{
    public required string Service { get; init; }

    public required string Layer { get; init; }

    public required string Operation { get; init; }
}

public sealed class EffectivePermissions
{
    public required string UserId { get; init; }

    public required IReadOnlyList<string> Roles { get; init; }

    public required IReadOnlyList<PermissionGrant> Permissions { get; init; }

    public DateTimeOffset ResolvedAt { get; init; } = DateTimeOffset.UtcNow;
}
