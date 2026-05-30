// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Content-hash-keyed artifact cache for spec compute outputs.
/// </summary>
/// <remarks>
/// Keys are produced by <c>SpecContentHashCalculator</c>:
/// <c>sha256(grammar_version || process_family_version || node_id || node_kind
/// || node_op || (canonical_fragment OR sorted(parameters) || sorted(source_pins)
/// || sorted(inputs-keyed-by-param-name)) || sorted(input_hashes))</c>. The
/// canonical-fragment branch is used when the upstream has emitted a
/// canonicalised JSON fragment; otherwise the calculator falls back to a
/// deterministic serialisation of the node's parameter, source-pin, and
/// inputs bags. The fallback emits each inputs entry as
/// <c>paramName=R:&lt;upstream-hash&gt;</c> for resolved <c>@node</c>
/// references or <c>paramName=L:&lt;literal&gt;</c> for scalar inputs so that
/// swapping two <c>@node</c> bindings or mutating a scalar input yields a
/// distinct cache key even when the upstream hash set is unchanged. Entries
/// are addressable via <see cref="CachedArtifactRef.Uri"/>. Mutable sources
/// without pinned versions receive TTL-backed entries; all other entries live
/// until the content hash changes.
/// </remarks>
public interface IContentHashArtifactCache
{
    /// <summary>
    /// Returns the cached artifact reference for the given content hash, or
    /// <c>null</c> if not present.
    /// </summary>
    Task<CachedArtifactRef?> TryGetAsync(string contentHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a stream over the artifact bytes. Returns <c>null</c> when the
    /// content hash is unknown or has been evicted.
    /// </summary>
    Task<Stream?> OpenReadAsync(string contentHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes an artifact into the cache and returns the reference clients
    /// use to read it back. Lifetime depends on the implementation — the S1
    /// default (<c>InMemoryContentHashArtifactCache</c>) is process-local and
    /// does not survive a restart; durable CloudFileStorage-backed variants
    /// are a follow-on.
    /// </summary>
    Task<CachedArtifactRef> PutAsync(SpecArtifactPayload payload, CancellationToken cancellationToken = default);
}
