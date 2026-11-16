// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

public sealed class FieldMaskPolicy
{
    public Guid PolicyId { get; init; } = Guid.NewGuid();

    public required string Role { get; init; }

    public required string Service { get; init; }

    public required string Layer { get; init; }

    public required string Attribute { get; init; }

    public string? Description { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
