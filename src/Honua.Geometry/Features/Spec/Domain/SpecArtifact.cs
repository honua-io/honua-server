// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Metadata reference returned by the content-hash artifact cache for a
/// previously-materialised node output.
/// </summary>
public sealed record CachedArtifactRef
{
    /// <summary>
    /// Content hash of the artifact (the cache key).
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Stable retrieval URI. For S1 this is the
    /// <c>/v1/spec/artifact/{hash}</c> endpoint. Transport-neutral so that
    /// an admin preview can consume it directly.
    /// </summary>
    public required string Uri { get; init; }

    /// <summary>
    /// Size in bytes of the cached artifact.
    /// </summary>
    public required long Bytes { get; init; }

    /// <summary>
    /// UTC timestamp the artifact was produced.
    /// </summary>
    public required DateTimeOffset ProducedAt { get; init; }

    /// <summary>
    /// Expiration timestamp for TTL-backed entries (mutable sources); null
    /// for content-hash entries that never expire.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Optional content-type hint for the stored payload (e.g. <c>application/x-arrow</c>).
    /// </summary>
    public string? ContentType { get; init; }
}

/// <summary>
/// Payload produced by a single compute node — used as the argument to
/// <see cref="Honua.Core.Features.Spec.Abstractions.IContentHashArtifactCache.PutAsync"/>.
/// </summary>
public sealed record SpecArtifactPayload
{
    /// <summary>
    /// Content hash (cache key) computed from the spec fragment and inputs.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Raw bytes of the artifact. S1 stores these in the cache verbatim.
    /// </summary>
    public required ReadOnlyMemory<byte> Bytes { get; init; }

    /// <summary>
    /// Optional content-type hint.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// TTL for this artifact. Null means the entry lives as long as the
    /// content-hash remains valid (never auto-expires).
    /// </summary>
    public TimeSpan? Ttl { get; init; }
}
