// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Read access to the canonical Metadata v2 graph snapshot.
/// Consumers should call <see cref="GetCurrentAsync"/> once per request and reuse
/// the returned snapshot — it is immutable for its revision.
/// </summary>
public interface IMetadataV2GraphProvider
{
    /// <summary>
    /// Returns the current Metadata v2 graph snapshot.
    /// </summary>
    ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a specific revision of the graph if it is retained.
    /// </summary>
    ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-write access to the canonical Metadata v2 graph snapshot.
/// </summary>
public interface IMetadataV2GraphStore : IMetadataV2GraphProvider
{
    /// <summary>
    /// Persists a new graph snapshot. The returned snapshot reflects the persisted state.
    /// </summary>
    /// <param name="graph">Graph document to persist. The store allocates and returns its durable revision.</param>
    /// <param name="expectedEtag">Optional optimistic concurrency tag; pass null to force write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MetadataV2GraphSnapshot> SaveAsync(
        MetadataV2Graph graph,
        string? expectedEtag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes an existing retained revision current without creating a new snapshot.
    /// </summary>
    /// <param name="revision">Retained durable revision to activate.</param>
    /// <param name="expectedCurrentEtag">Optional optimistic concurrency tag for the current pointer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<MetadataV2GraphSnapshot> ActivateRevisionAsync(
        long revision,
        string? expectedCurrentEtag,
        CancellationToken cancellationToken = default);
}
