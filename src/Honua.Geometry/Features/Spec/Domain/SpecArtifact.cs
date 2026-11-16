// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

public sealed record CachedArtifactRef
{
    public required string ContentHash { get; init; }

    public required string Uri { get; init; }

    public required long Bytes { get; init; }

    public required DateTimeOffset ProducedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string? ContentType { get; init; }
}

public sealed record SpecArtifactPayload
{
    public required string ContentHash { get; init; }

    public required ReadOnlyMemory<byte> Bytes { get; init; }

    public string? ContentType { get; init; }

    public TimeSpan? Ttl { get; init; }
}
