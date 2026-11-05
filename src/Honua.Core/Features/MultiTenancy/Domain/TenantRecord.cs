// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.MultiTenancy.Domain;

public sealed record TenantRecord
{
    public required string TenantId { get; init; }

    public required string DisplayName { get; init; }

    public required TenantStatus Status { get; init; }

    public string? Plan { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? SuspendedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }

    public TenantStatus PriorStatus { get; init; } = TenantStatus.Active;
}
