// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Content-hash-keyed artifact cache for spec compute outputs.
/// </summary>
/// <remarks>
/// The cache is keyed by <c>sha256(grammar_version || process_family_version
/// || canonical_spec_fragment || sorted(input_hashes))</c>. Entries are
/// addressable via <see cref="CachedArtifactRef.Uri"/>. Mutable sources
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
    /// Writes an artifact into the cache. Returns the durable reference.
    /// </summary>
    Task<CachedArtifactRef> PutAsync(SpecArtifactPayload payload, CancellationToken cancellationToken = default);
}
