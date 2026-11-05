// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.RateLimiting.Domain;

public sealed class RateLimitPolicy
{
    public Guid PolicyId { get; init; } = Guid.NewGuid();

    public required string Name { get; init; }

    public required string Scope { get; init; }

    public required string Key { get; init; }

    public required int RequestsPerWindow { get; init; }

    public required TimeSpan WindowDuration { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class RateLimitStatus
{
    public required Guid PolicyId { get; init; }

    public required string Key { get; init; }

    public required int RequestsConsumed { get; init; }

    public required int RequestsRemaining { get; init; }

    public required DateTimeOffset WindowResetsAt { get; init; }
}
