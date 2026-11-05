// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class CacheStatusResponse
{
    public required bool IsHealthy { get; init; }

    public required bool IsUsingFallback { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}

public sealed class CacheInvalidationRequest
{
    public required string Scope { get; init; }

    public string? ServiceId { get; init; }

    public int? LayerId { get; init; }

    public string? CollectionId { get; init; }
}

public sealed class CacheInvalidationResponse
{
    public required bool Success { get; init; }

    public required string Scope { get; init; }

    public required string Message { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}
