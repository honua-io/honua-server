// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

/// <summary>
/// Read and allocation surface for topology generations (#2716), the mutable-editing
/// companion to the immutable-generation lifecycle defined by #2715. This store never
/// changes a dataset's active generation pointer — it only lists/gets generation metadata
/// and allocates a new, empty <c>draft</c> generation an operator can then target with
/// <see cref="INetworkTopologyEditStore"/>.
/// </summary>
public interface INetworkTopologyGenerationStore
{
    /// <summary>
    /// Lists every topology generation recorded for a dataset, newest generation first.
    /// </summary>
    Task<IReadOnlyList<NetworkTopologyGeneration>> ListAsync(
        string datasetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single generation by dataset id and generation number, or <c>null</c> when it
    /// does not exist.
    /// </summary>
    Task<NetworkTopologyGeneration?> GetAsync(
        string datasetId,
        long generation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Allocates a new <c>draft</c> generation for a dataset, seeded from the dataset's
    /// current active generation (source revision, edge/vertex table, and SRID). The active
    /// generation pointer is never changed by this call. Throws
    /// <see cref="NetworkTopologyActiveGenerationMissingException"/> when the dataset has no
    /// active generation to seed from (should not happen for a registered dataset, but is
    /// guarded rather than assumed).
    /// </summary>
    Task<NetworkTopologyGeneration> AllocateDraftAsync(
        string datasetId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when a draft generation cannot be allocated because the dataset has no active
/// generation to seed from. This violates the invariant migration 084 establishes
/// (exactly one active generation per registered dataset) and indicates a corrupted or
/// unregistered dataset rather than a normal validation failure.
/// </summary>
public sealed class NetworkTopologyActiveGenerationMissingException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkTopologyActiveGenerationMissingException"/> class.
    /// </summary>
    public NetworkTopologyActiveGenerationMissingException(string datasetId)
        : base($"Network dataset '{datasetId}' has no active topology generation to seed a draft from.")
        => DatasetId = datasetId;

    /// <summary>Gets the affected dataset id.</summary>
    public string DatasetId { get; }
}
