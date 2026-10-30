// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Cheap read of the durable identity (ETag) of the active Metadata v2 graph revision, without
/// loading the graph document. Lets a node that has no cross-node invalidation channel (for
/// example a Lambda instance without Redis) confirm that its cached snapshot is still current
/// before serving it, so a publish on another instance is visible on the next request instead of
/// after the cache TTL.
/// </summary>
/// <remarks>
/// Implementations must be a single short read with no session state (no <c>SET</c>, advisory
/// locks or named prepared statements) so they never pin a pooled connection behind a proxy.
/// </remarks>
public interface IMetadataV2GraphRevisionProbe
{
    /// <summary>
    /// Returns the ETag of the active revision, or <c>null</c> when no revision has been
    /// activated for the environment (the reader then serves a compatibility or empty snapshot,
    /// which this probe cannot version).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<string?> GetCurrentEtagAsync(CancellationToken cancellationToken = default);
}
