// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Drops any in-process cached Metadata v2 graph snapshot for an environment so the next
/// read reloads it from the backing store.
/// </summary>
/// <remarks>
/// The canonical read path resolves services/layers through <see cref="IMetadataV2GraphProvider"/>,
/// which the shared caching decorator wraps with a short-TTL, per-instance snapshot cache. The
/// single canonical catalog write (<c>IMetadataV2GraphStore.SaveAsync</c>) invalidates that cache
/// on the writing node so read surfaces observe the mutation immediately instead of waiting out
/// the TTL. Multi-node deployments still bound cross-node staleness with the TTL because a save on
/// one node does not reach the others' in-process caches; the TTL is the correctness backstop and
/// this hook is the same-node fast path. Read-modify-write callers do not depend on this: they read
/// the persisted snapshot directly through the uncached store, never the cached provider.
/// </remarks>
public interface IMetadataV2GraphCacheInvalidator
{
    /// <summary>
    /// Invalidates the cached snapshot for the supplied environment.
    /// </summary>
    /// <param name="environment">The metadata environment whose cached snapshot should be dropped.</param>
    void Invalidate(string environment);

    /// <summary>
    /// Invalidates every cached snapshot across all environments.
    /// </summary>
    void InvalidateAll();
}
